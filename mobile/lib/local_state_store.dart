import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'models.dart';
import 'native_storage_gate.dart';
import 'package:uuid/uuid.dart';

class LocalStateLoadException implements Exception {
  final String message;
  LocalStateLoadException(this.message);
  @override
  String toString() => message;
}

/// Serial writes, an OS lock shared with other Flutter engines, atomic replacement
/// and a validated backup. A malformed file is never replaced by welcome data.
class LocalStateStore {
  final File file;
  Future<void> _tail = Future.value();
  bool _blocked = false;
  LocalStateStore(this.file);
  File get backup => File('${file.path}.backup');

  Future<T> _transaction<T>(Future<T> Function() action) {
    final result = _tail.then((_) async {
      await file.parent.create(recursive: true);
      final lease = await NativeStorageGate.acquire();
      RandomAccessFile? lock;
      var locked = false;
      try {
        lock = await File('${file.path}.lock').open(mode: FileMode.append);
        await lock.lock(FileLock.blockingExclusive);
        locked = true;
        return await action();
      } finally {
        if (locked) await lock!.unlock();
        await lock?.close();
        await NativeStorageGate.release(lease);
      }
    });
    _tail = result.then<void>((_) {}, onError: (Object error, StackTrace stack) {});
    return result;
  }

  AppState _decode(String text) {
    final json = Map<String, dynamic>.from(jsonDecode(text) as Map);
    SyncJson.validate(json);
    return AppState.fromJson(json);
  }

  Future<AppState?> _read() async {
    final primaryExists = await file.exists();
    final backupExists = await backup.exists();
    if (!primaryExists && !backupExists) { _blocked = false; return null; }
    if (primaryExists) {
      try {
        final state = _decode(await file.readAsString());
        _blocked = false;
        return state;
      } catch (_) { /* Try only a validated backup; never silently reset. */ }
    }
    if (backupExists) {
      try {
        final state = _decode(await backup.readAsString());
        if (primaryExists) {
          await file.copy('${file.path}.corrupt-${DateTime.now().microsecondsSinceEpoch}.json');
        }
        _blocked = false;
        state.recoveryMessage = '已从本地备份恢复。损坏的原文件已保留，可用于进一步恢复。';
        return state;
      } catch (_) { /* Both copies are unusable; leave them untouched. */ }
    }
    _blocked = true;
    throw LocalStateLoadException('无法读取本地数据，已保留原文件并停止写入。请恢复有效备份后重试。');
  }

  Future<AppState> load() => _transaction(() async {
    final state = await _read();
    if (state != null) return state;
    final initial = AppState(papers: [PaperData(title: '我的待办', items: [
      PaperItem(text: '欢迎使用 PaperTodo 手机端'),
      PaperItem(text: '在设置中配置云同步，与电脑保持一致'),
    ])]);
    await _atomicWrite(file, jsonEncode(initial.toJson()));
    return initial;
  });

  Future<void> _atomicWrite(File destination, String text) async {
    final temporary = File('${destination.path}.tmp-${const Uuid().v4()}');
    try {
      await temporary.writeAsString(text, flush: true);
      await temporary.rename(destination.path);
    } finally {
      if (await temporary.exists()) await temporary.delete();
    }
  }

  Future<void> save(AppState state) {
    // Snapshot before entering the async queue, so callers cannot mutate a queued write.
    final snapshot = jsonEncode(state.toJson());
    return _transaction(() async {
      final existing = await _read();
      if (_blocked) throw LocalStateLoadException('本地数据未恢复，保存已停止。');
      final requested = _decode(snapshot);
      final merged = existing == null ? requested : DataMergeEngine.merge(requested, existing).mergedState;
      if (existing != null) await _atomicWrite(backup, jsonEncode(existing.toJson()));
      await _atomicWrite(file, const JsonEncoder.withIndent('  ').convert(merged.toJson()));
    });
  }

  Future<AppState> restore(String contents) => _transaction(() async {
    // Only called after the user chooses a backup and confirms recovery.
    final restored = SyncJson.parse(contents);
    final stamp = DateTime.now().microsecondsSinceEpoch;
    if (await file.exists()) await file.copy('${file.path}.before-restore-$stamp.json');
    if (await backup.exists()) await backup.copy('${backup.path}.before-restore-$stamp.json');
    final json = const JsonEncoder.withIndent('  ').convert(restored.toJson());
    await _atomicWrite(backup, json);
    await _atomicWrite(file, json);
    _blocked = false;
    return restored;
  });
}

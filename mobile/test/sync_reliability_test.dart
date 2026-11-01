import 'dart:convert';
import 'dart:io';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/models.dart';
import 'package:mobile/local_state_store.dart';
import 'package:mobile/sync_service.dart';
import 'package:path_provider_platform_interface/path_provider_platform_interface.dart';

class TestPaths extends PathProviderPlatform {
  final String path;
  TestPaths(this.path);
  @override
  Future<String?> getApplicationDocumentsPath() async => path;
}

void main() {
  test('Incoming deletion records are data changes, not configuration cancellation', () async {
    final dir = await Directory.systemTemp.createTemp('papertodo-tombstone-test-');
    final previous = PathProviderPlatform.instance;
    PathProviderPlatform.instance = TestPaths(dir.path);
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      request.response.headers.contentType = ContentType.json;
      if (request.method == 'GET') {
        request.response.write(jsonEncode({'schemaVersion': 1, 'papers': [], 'syncTombstones': [
          {'id': 'removed-paper', 'type': 'paper', 'deletedAt': 1},
        ]}));
      } else { await request.drain<void>(); request.response.write('{}'); }
      await request.response.close();
    });
    try {
      final state = AppState(syncEnabled: true, syncProvider: 'custom', syncCustomServerUrl: 'http://127.0.0.1:${server.port}',
          papers: [PaperData(id: 'local-paper')]);
      final result = await SyncManager.sync(state);
      expect(result.success, isTrue);
      expect(state.syncTombstones.single.id, 'removed-paper');
      expect(state.lastSyncTimeUtc, greaterThan(0));
    } finally {
      await server.close(force: true);
      PathProviderPlatform.instance = previous;
      await dir.delete(recursive: true);
    }
  });

  test('Failed upload is not reported successful and contains no credentials', () async {
    final dir = await Directory.systemTemp.createTemp('papertodo-sync-test-');
    final previous = PathProviderPlatform.instance;
    PathProviderPlatform.instance = TestPaths(dir.path);
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    String? payload;
    server.listen((request) async {
      if (request.method == 'GET') { request.response.statusCode = 404; }
      else { payload = await utf8.decoder.bind(request).join(); request.response.statusCode = 403; }
      await request.response.close();
    });
    try {
      final state = AppState(syncEnabled: true, syncProvider: 'custom', syncCustomToken: 'private-token',
          syncS3SecretKey: 'private-key', syncCustomServerUrl: 'http://127.0.0.1:${server.port}',
          papers: [PaperData(id: 'p', content: 'saved')]);
      final result = await SyncManager.sync(state);
      expect(result.success, isFalse);
      expect(state.lastSyncTimeUtc, 0);
      expect(payload, isNotNull);
      expect(payload, isNot(contains('private')));
      expect((await SyncManager.loadLocalState()).papers.single.content, 'saved');
    } finally {
      await server.close(force: true);
      PathProviderPlatform.instance = previous;
      await dir.delete(recursive: true);
    }
  });

  test('Corrupt data remains untouched and saving cannot silently reset it', () async {
    final dir = await Directory.systemTemp.createTemp('papertodo-corrupt-test-');
    try {
      final file = File('${dir.path}/data.json');
      const original = '{"papers":[{"id":"recoverable","content":"important"';
      await file.writeAsString(original);
      final store = LocalStateStore(file);
      await expectLater(store.load(), throwsA(isA<LocalStateLoadException>()));
      await expectLater(store.save(AppState()), throwsA(isA<LocalStateLoadException>()));
      expect(await file.readAsString(), original);
      final restored = await store.restore(jsonEncode(SyncJson.document(AppState(papers: [PaperData(id: 'restored', content: 'from backup')]))));
      expect(restored.papers.single.content, 'from backup');
      expect((await store.load()).papers.single.id, 'restored');
      final originals = await dir.list().where((entry) => entry.path.contains('.before-restore-')).toList();
      expect(originals, hasLength(1));
      expect(await File(originals.single.path).readAsString(), original);
    } finally { await dir.delete(recursive: true); }
  });

  test('Validated backup recovery and concurrent queued saves preserve latest edits', () async {
    final dir = await Directory.systemTemp.createTemp('papertodo-backup-test-');
    try {
      final file = File('${dir.path}/data.json');
      final store = LocalStateStore(file);
      final state = AppState(papers: [PaperData(id: 'p', content: 'one', updatedAt: 10)]);
      await store.save(state);
      final pending = <Future<void>>[];
      for (var i = 0; i < 12; i++) {
        state.papers[0]..content = '$i'..updatedAt = 20 + i;
        pending.add(store.save(state));
      }
      await Future.wait(pending);
      expect((await store.load()).papers.single.content, '11');
      await file.writeAsString('broken');
      final recovered = await store.load();
      expect(recovered.papers.single.content, '10');
      expect(recovered.recoveryMessage, isNotNull);
      expect(await file.readAsString(), 'broken');
      expect(await dir.list().any((entry) => entry.path.contains('.corrupt-')), isTrue);
      await store.save(recovered);
      expect((await store.load()).papers.single.content, '10');
    } finally { await dir.delete(recursive: true); }
  });

  test('Missing fields preserve canvas, explicit empty fields clear it, and references remain live', () {
    final paper = PaperData(id: 'p', updatedAt: 10, noteCanvasElements: [{'id': 'code', 'text': 'keep'}],
        items: [PaperItem(id: 'i', updatedAt: 10, todoColumnWidths: [0.3, 0.7])]);
    final state = AppState(papers: [paper]);
    final item = paper.items.single;
    final remote = SyncJson.parse('{"papers":[{"id":"p","content":"new","updatedAt":20,"items":[{"id":"i","done":true,"updatedAt":20}]}]}');
    final result = DataMergeEngine.merge(state, remote);
    SyncJson.apply(state, result.mergedState);
    expect(identical(state.papers.single, paper), isTrue);
    expect(identical(paper.items.single, item), isTrue);
    expect(paper.noteCanvasElements.single['text'], 'keep');
    expect(item.todoColumnWidths, [0.3, 0.7]);
    expect(paper.content, 'new');
    final deletion = SyncJson.parse('{"papers":[{"id":"p","updatedAt":30,"noteCanvasElements":[]}]}');
    expect(DataMergeEngine.merge(state, deletion).mergedState.papers.single.noteCanvasElements, isEmpty);
  });

  test('Item-only edits trigger upload; equal timestamps converge; old deletions remain', () {
    final a = AppState(papers: [PaperData(id: 'p', updatedAt: 10,
        items: [PaperItem(id: 'i', text: 'new', updatedAt: 20)])]);
    final b = AppState.fromJson(SyncJson.cloneMap(a.toJson()));
    b.papers[0].items[0]..text = 'old'..updatedAt = 10;
    expect(DataMergeEngine.merge(a, b).hasChangesToRemote, isTrue);
    a.syncTombstones.add(SyncTombstone(id: 'old-paper', type: 'paper', deletedAt: 1));
    expect(DataMergeEngine.merge(a, b).mergedState.syncTombstones.single.deletedAt, 1);
    a.papers[0].content = '中文😀'; b.papers[0].content = 'other';
    expect(SyncJson.canonical(SyncJson.document(DataMergeEngine.merge(a, b).mergedState)),
        SyncJson.canonical(SyncJson.document(DataMergeEngine.merge(b, a).mergedState)));
  });
}

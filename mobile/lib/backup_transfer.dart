import 'dart:convert';
import 'package:flutter/services.dart';
import 'models.dart';
import 'sync_service.dart';

class BackupTransfer {
  static const channel = MethodChannel('com.papertodo.mobile/backup');
  static Future<String?> readBackup() => channel.invokeMethod<String>('import');
  static Future<bool> exportPapers(AppState state) async {
    return await channel.invokeMethod<bool>('export', jsonEncode(SyncJson.document(state))) ?? false;
  }
  static Future<bool> importPapers(AppState state) async {
    final text = await readBackup();
    if (text == null) return false;
    final imported = SyncJson.parse(text);
    SyncJson.apply(state, DataMergeEngine.merge(state, imported).mergedState);
    await SyncManager.saveLocalState(state);
    return true;
  }
}

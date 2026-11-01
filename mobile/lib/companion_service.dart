import 'dart:convert';
import 'dart:io';
import 'package:flutter/services.dart';
import 'models.dart';
import 'sync_service.dart';

class CompanionService {
  static const channel = MethodChannel('com.papertodo.mobile/companion');
  static void listen(Future<void> Function()? onActionsChanged) {
    if (!Platform.isAndroid) return;
    channel.setMethodCallHandler(onActionsChanged == null ? null : (call) async {
      if (call.method == 'actionsChanged') await onActionsChanged();
    });
  }

  static Future<void> update(AppState state) async {
    if (!Platform.isAndroid) return;
    await SyncManager.refreshLocalState(state);
    // This cache is for native widgets and alarms. It never contains sync credentials.
    await channel.invokeMethod<void>('update', jsonEncode({
      'syncEnabled': state.syncEnabled,
      'notificationsEnabled': state.notificationsEnabled,
      'notificationLeadMinutes': state.notificationLeadMinutes,
      'notifyOverdue': state.notifyOverdue,
      'papers': [for (final p in state.papers.where((p) => p.type == 'todo' && !p.isDeleted)) {
        'id': p.id, 'title': p.title,
        'items': [for (final i in p.items.where((i) => !i.isDeleted)) {
          'id': i.id, 'text': i.text, 'done': i.done, 'dueAtLocal': i.dueAtLocal, 'updatedAt': i.updatedAt,
        }],
      }],
    }));
  }

  static Future<void> applyPendingActions(AppState state) async {
    if (!Platform.isAndroid) return;
    final encoded = await channel.invokeMethod<String>('pendingActions') ?? '[]';
    final actions = jsonDecode(encoded) as List;
    if (actions.isEmpty) return;
    final papers = {for (final p in state.papers) p.id: p};
    for (final raw in actions) {
      final action = raw as Map;
      final paper = papers[action['paperId']];
      if (paper == null) continue; // A delayed widget action must not resurrect a deleted paper.
      final items = {for (final i in paper.items) i.id: i};
      final item = items[action['itemId']];
      final timestamp = action['updatedAt'] as int;
      if (item == null || item.updatedAt > timestamp) continue;
      item.done = action['done'] as bool;
      item.updatedAt = timestamp;
    }
    await SyncManager.saveLocalState(state);
    await channel.invokeMethod<void>('ackActions', actions.map((a) => (a as Map)['id'] as String).toList());
  }
}

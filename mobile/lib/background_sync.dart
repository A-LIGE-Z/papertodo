import 'package:flutter/widgets.dart';
import 'companion_service.dart';
import 'sync_service.dart';

Future<void> runBackgroundSync() async {
  WidgetsFlutterBinding.ensureInitialized();
  var retry = false;
  try {
    final state = await SyncManager.loadLocalState();
    await CompanionService.applyPendingActions(state);
    if (state.syncEnabled) retry = !(await SyncManager.sync(state)).success;
    await CompanionService.update(state);
  } finally {
    await CompanionService.channel.invokeMethod<void>('backgroundComplete', retry);
  }
}

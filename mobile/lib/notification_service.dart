import 'package:flutter/services.dart';
import 'models.dart';
import 'companion_service.dart';

class NotificationService {
  static const MethodChannel _channel = MethodChannel('com.papertodo.mobile/notifications');
  static Future<bool> requestPermission() async {
    try { return await _channel.invokeMethod<bool>('requestPermission') ?? false; }
    on PlatformException { return false; }
    on MissingPluginException { return false; }
  }
  static Future<bool> hasPermission() async {
    try { return await _channel.invokeMethod<bool>('hasPermission') ?? false; }
    on PlatformException { return false; }
    on MissingPluginException { return false; }
  }
  static Future<void> checkDueReminders(AppState state) async {
    // Native alarms own delivery, including while no Flutter UI is running.
    await CompanionService.update(state);
  }
  static Future<bool> sendTestNotification() async {
    if (!await requestPermission()) return false;
    try {
      return await _channel.invokeMethod<bool>('showNotification', {
        'id': 9999, 'title': 'PaperTodo 提醒测试', 'body': '通知已送交系统。后台提醒可能受系统省电设置影响。',
      }) ?? false;
    } on PlatformException { return false; }
  }
}

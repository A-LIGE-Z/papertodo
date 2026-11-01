import 'dart:io';
import 'package:flutter/services.dart';
import 'package:uuid/uuid.dart';

class NativeStorageGate {
  static const channel = MethodChannel('com.papertodo.mobile/companion');
  static Future<String?> acquire() async {
    if (!Platform.isAndroid) return null;
    final lease = const Uuid().v4();
    await channel.invokeMethod<void>('storageAcquire', lease);
    return lease;
  }
  static Future<void> release(String? lease) async {
    if (lease != null) await channel.invokeMethod<void>('storageRelease', lease);
  }
}

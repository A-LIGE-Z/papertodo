import 'dart:convert';
import 'dart:io';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/sync_service.dart';

void main() {
  test('A public ping does not make an invalid Token pass', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final paths = <String>[];
    server.listen((request) async {
      paths.add(request.uri.path);
      if (request.uri.path == '/api/v1/ping') {
        request.response.write(jsonEncode({'app': 'PaperTodo.SyncServer', 'status': 'ok'}));
      } else { request.response.statusCode = 401; }
      await request.response.close();
    });
    try {
      final provider = CustomHttpSyncProvider(serverUrl: 'http://127.0.0.1:${server.port}', token: 'wrong');
      final result = await provider.testConnection();
      expect(result.success, isFalse);
      expect(result.message, contains('401'));
      expect(paths, contains('/api/v1/sync'));
    } finally { await server.close(force: true); }
  });

  test('A login HTML page is not a valid sync connection', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async { request.response.write('<html>Login required</html>'); await request.response.close(); });
    try {
      final result = await CustomHttpSyncProvider(serverUrl: 'http://127.0.0.1:${server.port}', token: 'token').testConnection();
      expect(result.success, isFalse);
    } finally { await server.close(force: true); }
  });

  test('An authenticated data response passes the connection test', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      if (request.headers.value('Authorization') != 'Bearer valid') {
        request.response.statusCode = 401;
      } else {
        request.response.write('{"papers":[],"syncTombstones":[]}');
      }
      await request.response.close();
    });
    try {
      expect((await CustomHttpSyncProvider(serverUrl: 'http://127.0.0.1:${server.port}', token: 'valid').testConnection()).success, isTrue);
    } finally { await server.close(force: true); }
  });
}

import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:http/http.dart' as http;
import 'package:path_provider/path_provider.dart';
import 'models.dart';
import 'local_state_store.dart';

class SyncTestResult {
  final bool success;
  final String message;
  SyncTestResult(this.success, this.message);
}

abstract class SyncStorageProvider {
  Future<SyncTestResult> testConnection();
  Future<String?> downloadDataJson();
  Future<bool> uploadDataJson(String json);
}

class SyncConflictException implements Exception {
  @override
  String toString() => '云端数据已变化，请重新合并后同步。';
}

mixin RevisionedStorage {
  String? _etag;
  bool _missing = false;
  void observeRevision(int status, Map<String, String> headers) {
    _missing = status == 404;
    final tag = headers['etag'];
    _etag = tag != null && !tag.startsWith('W/') ? tag : null;
  }
  Map<String, String> get conditionalHeaders => {
    if (_missing) 'If-None-Match': '*',
    if (!_missing && _etag != null) 'If-Match': _etag!,
  };
  bool writeResult(int status, Map<String, String> headers) {
    if (status == 412) throw SyncConflictException();
    if (status < 200 || status >= 300) return false;
    observeRevision(status, headers);
    return true;
  }
}

class WebDavSyncProvider with RevisionedStorage implements SyncStorageProvider {
  final String url;
  final String username;
  final String password;
  final String remotePath;
  late final String _baseUrl;
  late final String _cleanRemotePath;
  late final Map<String, String> _headers;

  WebDavSyncProvider({
    required this.url,
    required this.username,
    required this.password,
    required this.remotePath,
  }) {
    _baseUrl = url.trim().replaceAll(RegExp(r'/+$'), '');
    var path = remotePath.trim();
    if (path.isEmpty) path = '/papertodo/data.json';
    if (!path.startsWith('/')) path = '/$path';
    _cleanRemotePath = path;

    _headers = {
      'User-Agent': 'PaperTodo-Android/0.0.2',
    };
    if (username.isNotEmpty) {
      final auth = base64Encode(utf8.encode('$username:$password'));
      _headers['Authorization'] = 'Basic $auth';
    }
  }

  Uri _getUri(String path) {
    final cleanPath = path.replaceFirst(RegExp(r'^/+'), '');
    return Uri.parse('$_baseUrl/$cleanPath');
  }

  @override
  Future<SyncTestResult> testConnection() async {
    if (_baseUrl.isEmpty) {
      return SyncTestResult(false, '服务器地址不能为空');
    }
    try {
      final uri = _getUri(_cleanRemotePath);
      final resp = await http.get(uri, headers: _headers).timeout(const Duration(seconds: 15));
      if (resp.statusCode == 200) {
        return SyncTestResult(true, '连接成功，找到云端数据文件');
      } else if (resp.statusCode == 404) {
        return SyncTestResult(true, '连接成功（云端文件尚未创建，同步时将自动创建）');
      } else if (resp.statusCode == 401 || resp.statusCode == 403) {
        return SyncTestResult(false, '认证失败，请检查账号和密码');
      }
      return SyncTestResult(false, 'HTTP ${resp.statusCode}: ${resp.reasonPhrase}');
    } catch (e) {
      return SyncTestResult(false, '连接错误: $e');
    }
  }

  @override
  Future<String?> downloadDataJson() async {
    final uri = _getUri(_cleanRemotePath);
    final resp = await http.get(uri, headers: _headers).timeout(const Duration(seconds: 20));
    observeRevision(resp.statusCode, resp.headers);
    if (resp.statusCode == 404) return null;
    if (resp.statusCode != 200) {
      throw HttpException('HTTP ${resp.statusCode} ${resp.reasonPhrase}');
    }
    return utf8.decode(resp.bodyBytes);
  }

  @override
  Future<bool> uploadDataJson(String json) async {
    final uri = _getUri(_cleanRemotePath);
    await _ensureParentDirs();

    final resp = await http.put(
      uri,
      headers: {
        ..._headers,
        ...conditionalHeaders,
        'Content-Type': 'application/json; charset=utf-8',
      },
      body: utf8.encode(json),
    ).timeout(const Duration(seconds: 25));

    if (resp.statusCode == 409) {
      await _ensureParentDirs();
      final retry = await http.put(
        uri,
        headers: {
          ..._headers,
          ...conditionalHeaders,
          'Content-Type': 'application/json; charset=utf-8',
        },
        body: utf8.encode(json),
      );
      return writeResult(retry.statusCode, retry.headers);
    }

    return writeResult(resp.statusCode, resp.headers);
  }

  Future<void> _ensureParentDirs() async {
    final parts = _cleanRemotePath.split('/').where((s) => s.isNotEmpty).toList();
    if (parts.length <= 1) return;
    parts.removeLast(); // drop file name

    String current = '';
    for (final seg in parts) {
      current += '/$seg';
      try {
        final uri = _getUri(current);
        final req = http.Request('MKCOL', uri)..headers.addAll(_headers);
        await req.send();
      } catch (_) {}
    }
  }
}

class S3SyncProvider with RevisionedStorage implements SyncStorageProvider {
  final String endpoint;
  final String bucket;
  final String accessKey;
  final String secretKey;
  final String region;
  static const String objectKey = 'papertodo/data.json';

  S3SyncProvider({
    required this.endpoint,
    required this.bucket,
    required this.accessKey,
    required this.secretKey,
    required this.region,
  });

  Uri _getObjectUri() {
    final cleanEndpoint = endpoint.trim().replaceAll(RegExp(r'/+$'), '');
    return Uri.parse('$cleanEndpoint/$bucket/$objectKey');
  }

  @override
  Future<SyncTestResult> testConnection() async {
    if (endpoint.isEmpty || bucket.isEmpty) {
      return SyncTestResult(false, 'S3 配置不完整（请填写 Endpoint 和 Bucket）');
    }
    try {
      final uri = _getObjectUri();
      final req = http.Request('HEAD', uri);
      _signRequest(req, uri, 'HEAD', []);
      final streamed = await req.send().timeout(const Duration(seconds: 15));
      if (streamed.statusCode == 200) {
        return SyncTestResult(true, '连接成功，找到云端数据文件');
      } else if (streamed.statusCode == 404) {
        return SyncTestResult(true, '连接成功（云端文件尚未创建，同步时将自动生成）');
      } else if (streamed.statusCode == 401 || streamed.statusCode == 403) {
        return SyncTestResult(false, '认证失败，请检查 AccessKey/SecretKey');
      }
      return SyncTestResult(false, 'HTTP ${streamed.statusCode}');
    } catch (e) {
      return SyncTestResult(false, '连接错误: $e');
    }
  }

  @override
  Future<String?> downloadDataJson() async {
    final uri = _getObjectUri();
    final req = http.Request('GET', uri);
    _signRequest(req, uri, 'GET', []);
    final streamed = await req.send().timeout(const Duration(seconds: 20));
    observeRevision(streamed.statusCode, streamed.headers);
    if (streamed.statusCode == 404) { await streamed.stream.drain<void>(); return null; }
    final resp = await http.Response.fromStream(streamed);
    if (resp.statusCode != 200) {
      throw HttpException('HTTP ${resp.statusCode}: ${resp.reasonPhrase}');
    }
    return utf8.decode(resp.bodyBytes);
  }

  @override
  Future<bool> uploadDataJson(String json) async {
    final uri = _getObjectUri();
    final bytes = utf8.encode(json);
    final req = http.Request('PUT', uri)
      ..headers['Content-Type'] = 'application/json'
      ..bodyBytes = bytes;

    req.headers.addAll(conditionalHeaders);
    _signRequest(req, uri, 'PUT', bytes);
    final streamed = await req.send().timeout(const Duration(seconds: 25));
    await streamed.stream.drain<void>();
    return writeResult(streamed.statusCode, streamed.headers);
  }

  void _signRequest(http.Request req, Uri uri, String method, List<int> payload) {
    final now = DateTime.now().toUtc();
    final amzDate = '${now.year.toString().padLeft(4, '0')}'
        '${now.month.toString().padLeft(2, '0')}'
        '${now.day.toString().padLeft(2, '0')}T'
        '${now.hour.toString().padLeft(2, '0')}'
        '${now.minute.toString().padLeft(2, '0')}'
        '${now.second.toString().padLeft(2, '0')}Z';
    final dateStamp = amzDate.substring(0, 8);

    req.headers['x-amz-date'] = amzDate;
    final payloadHash = sha256.convert(payload).toString();
    req.headers['x-amz-content-sha256'] = payloadHash;

    final host = uri.host + (uri.hasPort && uri.port != 80 && uri.port != 443 ? ':${uri.port}' : '');
    req.headers['Host'] = host;

    final canonicalUri = uri.path;
    final canonicalHeaders = 'host:$host\nx-amz-content-sha256:$payloadHash\nx-amz-date:$amzDate\n';
    const signedHeaders = 'host;x-amz-content-sha256;x-amz-date';

    final canonicalRequest = '$method\n$canonicalUri\n\n$canonicalHeaders\n$signedHeaders\n$payloadHash';
    final canonicalRequestHash = sha256.convert(utf8.encode(canonicalRequest)).toString();

    final cleanRegion = region.isEmpty ? 'us-east-1' : region;
    final credentialScope = '$dateStamp/$cleanRegion/s3/aws4_request';
    final stringToSign = 'AWS4-HMAC-SHA256\n$amzDate\n$credentialScope\n$canonicalRequestHash';

    final kSecret = utf8.encode('AWS4$secretKey');
    final kDate = Hmac(sha256, kSecret).convert(utf8.encode(dateStamp)).bytes;
    final kRegion = Hmac(sha256, kDate).convert(utf8.encode(cleanRegion)).bytes;
    final kService = Hmac(sha256, kRegion).convert(utf8.encode('s3')).bytes;
    final kSigning = Hmac(sha256, kService).convert(utf8.encode('aws4_request')).bytes;

    final signature = Hmac(sha256, kSigning).convert(utf8.encode(stringToSign)).toString();
    final authHeader = 'AWS4-HMAC-SHA256 Credential=$accessKey/$credentialScope, SignedHeaders=$signedHeaders, Signature=$signature';

    req.headers['Authorization'] = authHeader;
  }
}

class CustomHttpSyncProvider with RevisionedStorage implements SyncStorageProvider {
  final String serverUrl;
  final String token;

  CustomHttpSyncProvider({required this.serverUrl, required this.token});

  Uri _getUri(String path) {
    final clean = serverUrl.trim().replaceAll(RegExp(r'/+$'), '');
    return Uri.parse('$clean/${path.replaceFirst(RegExp(r'^/+'), '')}');
  }

  Map<String, String> get _headers => {
        'User-Agent': 'PaperTodo-Android/0.0.2',
        if (token.isNotEmpty) 'Authorization': 'Bearer $token',
      };

  @override
  Future<SyncTestResult> testConnection() async {
    if (serverUrl.isEmpty) {
      return SyncTestResult(false, '服务器地址不能为空');
    }
    try {
      // Older servers expose /ping publicly; it cannot verify the Token.
      final resp = await http.get(_getUri('/api/v1/sync'), headers: _headers).timeout(const Duration(seconds: 15));
      if (resp.statusCode == 200) {
        SyncJson.parse(utf8.decode(resp.bodyBytes));
        return SyncTestResult(true, '连接成功，已验证数据读取权限');
      }
      if (resp.statusCode == 401 || resp.statusCode == 403) {
        return SyncTestResult(false, '认证失败（HTTP ${resp.statusCode}），请核对服务地址及访问密钥 Token。');
      }
      if (resp.statusCode == 404) {
        final ping = await http.get(_getUri('/api/v1/ping'), headers: _headers).timeout(const Duration(seconds: 10));
        final body = ping.statusCode == 200 ? jsonDecode(utf8.decode(ping.bodyBytes)) : null;
        if (body is Map && body['app'] == 'PaperTodo.SyncServer') {
          return SyncTestResult(true, '连接成功，云端数据文件尚未创建，首次同步时将验证写入权限');
        }
        return SyncTestResult(false, '数据接口不存在，请核对完整服务地址及应用路径。');
      }
      return SyncTestResult(false, 'HTTP ${resp.statusCode}');
    } on FormatException {
      return SyncTestResult(false, '服务器未返回有效的 PaperTodo 数据，请核对完整服务地址及应用路径。');
    } catch (e) {
      return SyncTestResult(false, '连接错误: $e');
    }
  }

  @override
  Future<String?> downloadDataJson() async {
    final resp = await http.get(_getUri('/api/v1/sync'), headers: _headers).timeout(const Duration(seconds: 15));
    observeRevision(resp.statusCode, resp.headers);
    if (resp.statusCode == 404) return null;
    if (resp.statusCode == 401 || resp.statusCode == 403) {
      throw HttpException('认证失败（HTTP ${resp.statusCode}），请核对服务地址及访问密钥 Token。');
    }
    if (resp.statusCode != 200) throw HttpException('数据接口返回 HTTP ${resp.statusCode}，请核对服务地址。');
    return utf8.decode(resp.bodyBytes);
  }

  @override
  Future<bool> uploadDataJson(String json) async {
    final resp = await http.post(
      _getUri('/api/v1/sync'),
      headers: {
        ..._headers,
        ...conditionalHeaders,
        'Content-Type': 'application/json; charset=utf-8',
      },
      body: utf8.encode(json),
    ).timeout(const Duration(seconds: 20));
    return writeResult(resp.statusCode, resp.headers);
  }
}

class SyncManager {
  static final Map<String, LocalStateStore> _stores = {};
  static final Expando<SyncChangeTracker> _trackers = Expando();
  static Future<SyncTestResult>? _inFlight;

  static SyncChangeTracker _tracker(AppState state) =>
      _trackers[state] ??= (SyncChangeTracker()..capture(state));

  static SyncStorageProvider createProvider(AppState state) {
    switch (state.syncProvider) {
      case 's3':
        return S3SyncProvider(endpoint: state.syncS3Endpoint, bucket: state.syncS3Bucket,
          accessKey: state.syncS3AccessKey, secretKey: state.syncS3SecretKey, region: state.syncS3Region);
      case 'custom':
        return CustomHttpSyncProvider(serverUrl: state.syncCustomServerUrl, token: state.syncCustomToken);
      default:
        return WebDavSyncProvider(url: state.syncWebDavUrl, username: state.syncWebDavUsername,
          password: state.syncWebDavPassword, remotePath: state.syncWebDavRemotePath);
    }
  }

  static Future<File> getLocalDataFile() async {
    final dir = await getApplicationDocumentsDirectory();
    return File('${dir.path}/data.json');
  }

  static Future<LocalStateStore> _store() async {
    final file = await getLocalDataFile();
    return _stores.putIfAbsent(file.path, () => LocalStateStore(file));
  }

  static Future<AppState> loadLocalState() async {
    final state = await (await _store()).load();
    _trackers[state] = SyncChangeTracker()..capture(state);
    return state;
  }

  static Future<void> saveLocalState(AppState state) async {
    _tracker(state).update(state);
    await (await _store()).save(state);
  }

  static Future<AppState> restoreLocalState(String contents) async {
    final state = await (await _store()).restore(contents);
    _trackers[state] = SyncChangeTracker()..capture(state);
    return state;
  }

  static Future<void> refreshLocalState(AppState state) async {
    _tracker(state).update(state);
    final disk = await (await _store()).load();
    SyncJson.apply(state, DataMergeEngine.merge(state, disk).mergedState);
    _tracker(state).capture(state);
  }

  static Future<SyncTestResult> sync(AppState state) {
    final running = _inFlight;
    if (running != null) return running;
    final operation = _retrySync(state);
    _inFlight = operation;
    return operation.whenComplete(() {
      if (identical(_inFlight, operation)) _inFlight = null;
    });
  }

  static Future<SyncTestResult> _retrySync(AppState state) async {
    for (var attempt = 0; ; attempt++) {
      try { return await _sync(state); }
      on SyncConflictException catch (error) {
        if (attempt < 2) continue;
        state.lastSyncStatus = error.toString();
        await saveLocalState(state);
        return SyncTestResult(false, state.lastSyncStatus);
      }
    }
  }

  static String _configuration(AppState state) => jsonEncode(
    state.toJson()..removeWhere((key, _) => !key.startsWith('sync') || key == 'syncTombstones'));

  static Future<SyncTestResult> _sync(AppState state) async {
    if (!state.syncEnabled) return SyncTestResult(false, '未开启同步');
    final configuration = _configuration(state);
    try {
      await saveLocalState(state);
      final provider = createProvider(state);
      final remoteJson = await provider.downloadDataJson();
      if (!state.syncEnabled || configuration != _configuration(state)) {
        return SyncTestResult(false, '同步配置已更改，请重试。');
      }
      await refreshLocalState(state);
      var needUpload = remoteJson == null;
      if (remoteJson != null) {
        final remote = SyncJson.parse(remoteJson);
        final merged = DataMergeEngine.merge(state, remote);
        SyncJson.apply(state, merged.mergedState);
        _tracker(state).capture(state);
        final remoteKeys = (jsonDecode(remoteJson) as Map).keys;
        needUpload = merged.hasChangesToLocal || merged.hasChangesToRemote ||
            remoteKeys.any((key) => !{'schemaVersion', 'papers', 'syncTombstones'}.contains(key));
      }
      // Persist the merge before networking again; the previous local copy is backed up.
      await saveLocalState(state);
      if (needUpload && !await provider.uploadDataJson(jsonEncode(SyncJson.document(state)))) {
        throw const HttpException('云端拒绝写入，本地更改已保留，等待下次同步。');
      }
      if (!state.syncEnabled || configuration != _configuration(state)) {
        return SyncTestResult(false, '同步配置已更改，请重试。');
      }
      state.lastSyncTimeUtc = DateTime.now().millisecondsSinceEpoch;
      state.lastSyncStatus = '同步成功';
      await saveLocalState(state);
      return SyncTestResult(true, state.lastSyncStatus);
    } on SyncConflictException {
      rethrow;
    } catch (error) {
      state.lastSyncStatus = '同步失败: $error';
      try { await saveLocalState(state); } catch (_) { /* Keep a failed load read-only. */ }
      return SyncTestResult(false, state.lastSyncStatus);
    }
  }
}

import 'dart:convert';
import 'models.dart';

class SyncJson {
  static const devicePaperFields = {
    'x', 'y', 'width', 'height', 'isVisible', 'alwaysOnTop', 'isCollapsed',
    'isPinnedToDesktop', 'textZoom', 'capsuleSide', 'capsuleMonitorDeviceName',
  };
  static Map<String, dynamic> cloneMap(Map<String, dynamic> value) =>
      Map<String, dynamic>.from(jsonDecode(jsonEncode(value)) as Map);
  static Map<String, dynamic> item(PaperItem value) {
    final json = value.toJson();
    if (value.presentSyncFields != null) {
      json.removeWhere((key, _) => !value.presentSyncFields!.contains(key));
    }
    return json;
  }
  static Map<String, dynamic> paper(PaperData value, {bool includeItems = true}) {
    final json = value.toJson();
    if (value.presentSyncFields != null) {
      json.removeWhere((key, _) => !value.presentSyncFields!.contains(key));
    }
    json.removeWhere((key, _) => devicePaperFields.contains(key) || key == 'items');
    if (includeItems) {
      final items = [...value.items]..sort((a, b) => a.id.compareTo(b.id));
      json['items'] = items.map(item).toList();
    }
    return json;
  }
  static String tombstoneKey(SyncTombstone t) => '${t.type}:${t.parentPaperId ?? ''}:${t.id}';
  static Map<String, dynamic> document(AppState state) {
    final papers = [...state.papers]..sort((a, b) => a.id.compareTo(b.id));
    final tombstones = [...state.syncTombstones]
      ..sort((a, b) => tombstoneKey(a).compareTo(tombstoneKey(b)));
    return {
      'schemaVersion': 1,
      'papers': papers.map((p) => paper(p)).toList(),
      'syncTombstones': tombstones.map((t) => t.toJson()).toList(),
    };
  }
  static void validate(Map<String, dynamic> root) {
    if (root['papers'] is! List) throw const FormatException('缺少纸片列表，原数据未修改。');
    final paperIds = <String>{};
    for (final raw in root['papers'] as List) {
      if (raw is! Map || raw['id'] is! String || (raw['id'] as String).isEmpty ||
          !paperIds.add(raw['id'] as String)) {
        throw const FormatException('纸片标识缺失或重复，原数据未修改。');
      }
      if (raw['items'] != null && raw['items'] is! List) throw const FormatException('待办列表格式无效。');
      final itemIds = <String>{};
      for (final entry in (raw['items'] as List? ?? [])) {
        if (entry is! Map || entry['id'] is! String || (entry['id'] as String).isEmpty ||
            !itemIds.add(entry['id'] as String)) {
          throw const FormatException('待办标识缺失或重复，原数据未修改。');
        }
      }
    }
    if (root['syncTombstones'] != null && root['syncTombstones'] is! List) {
      throw const FormatException('删除记录格式无效。');
    }
  }
  static AppState parse(String text) {
    final root = Map<String, dynamic>.from(jsonDecode(text) as Map);
    validate(root);
    final state = AppState.fromJson({'papers': root['papers'], 'syncTombstones': root['syncTombstones'] ?? []});
    final papers = root['papers'] as List;
    for (var n = 0; n < state.papers.length; n++) {
      final raw = Map<String, dynamic>.from(papers[n] as Map);
      final p = state.papers[n];
      p.presentSyncFields = raw.keys.toSet();
      p.updatedAt = raw['updatedAt'] as int? ?? 0;
      final items = raw['items'] as List? ?? [];
      for (var i = 0; i < p.items.length; i++) {
        final itemRaw = Map<String, dynamic>.from(items[i] as Map);
        p.items[i].presentSyncFields = itemRaw.keys.toSet();
        p.items[i].updatedAt = itemRaw['updatedAt'] as int? ?? p.updatedAt;
      }
    }
    return state;
  }
  static String canonical(dynamic value) {
    String stringKey(String text) =>
        's${text.codeUnits.map((c) => c.toRadixString(16).padLeft(4, '0')).join()}';
    if (value == null) return 'null';
    if (value is String) return stringKey(value);
    if (value is Map) {
      final keys = value.keys.cast<String>().toList()..sort();
      return '{${keys.map((k) => '${stringKey(k)}:${canonical(value[k])}').join(',')}}';
    }
    if (value is List) return '[${value.map(canonical).join(',')}]';
    if (value is double && value == value.truncateToDouble()) return value.toInt().toString();
    return value.toString();
  }
  static Map<String, dynamic> mergeFields(
      Map<String, dynamic> local, Map<String, dynamic> remote, int localTime, int remoteTime) {
    final remoteWins = remoteTime > localTime ||
        remoteTime == localTime && canonical(remote).compareTo(canonical(local)) > 0;
    return cloneMap(remoteWins ? {...local, ...remote} : {...remote, ...local});
  }
  // Dialogs and editors hold these objects. Preserve their identities during sync.
  static void apply(AppState state, AppState merged) {
    final existing = {for (final p in state.papers) p.id: p};
    state.papers = merged.papers.map((incoming) {
      final target = existing[incoming.id];
      if (target == null) return PaperData.fromJson(cloneMap(incoming.toJson()));
      final oldItems = {for (final i in target.items) i.id: i};
      target.items = incoming.items.map((value) {
        final item = oldItems[value.id] ?? PaperItem(id: value.id);
        item
          ..text = value.text
          ..done = value.done
          ..order = value.order
          ..updatedAt = value.updatedAt
          ..isDeleted = value.isDeleted
          ..linkedNoteId = value.linkedNoteId
          ..dueAtLocal = value.dueAtLocal
          ..todoColumnCount = value.todoColumnCount
          ..todoExtraColumns = [...value.todoExtraColumns]
          ..todoColumnWidths = [...value.todoColumnWidths]
          ..reminderIntervalValue = value.reminderIntervalValue
          ..reminderIntervalUnit = value.reminderIntervalUnit
          ..extraFields = cloneMap(value.extraFields)
          ..presentSyncFields = null;
        return item;
      }).toList();
      target
        ..type = incoming.type
        ..title = incoming.title
        ..content = incoming.content
        ..updatedAt = incoming.updatedAt
        ..isDeleted = incoming.isDeleted
        ..noteCanvasElements = jsonDecode(jsonEncode(incoming.noteCanvasElements)) as List
        ..extraFields = cloneMap(incoming.extraFields)
        ..presentSyncFields = null;
      return target;
    }).toList();
    state.syncTombstones = merged.syncTombstones.map((t) => SyncTombstone.fromJson(t.toJson())).toList();
  }
}

class DataMergeEngine {
  static DataMergeResult merge(AppState local, AppState remote) {
    final tombstones = <String, SyncTombstone>{};
    void add(SyncTombstone t) {
      if (t.id.isEmpty || !{'paper', 'item'}.contains(t.type)) return;
      final key = SyncJson.tombstoneKey(t);
      if (tombstones[key] == null || tombstones[key]!.deletedAt < t.deletedAt) {
        tombstones[key] = SyncTombstone.fromJson(t.toJson());
      }
    }
    for (final state in [local, remote]) {
      for (final t in state.syncTombstones) { add(t); }
      for (final p in state.papers) {
        if (p.isDeleted) add(SyncTombstone(id: p.id, type: 'paper', deletedAt: p.updatedAt));
        for (final i in p.items.where((i) => i.isDeleted)) {
          add(SyncTombstone(id: i.id, parentPaperId: p.id, deletedAt: i.updatedAt));
        }
      }
    }
    bool deleted(String type, String id, String? parent, int timestamp) =>
        tombstones.values.any((t) => t.type == type && t.id == id &&
            (type == 'paper' || t.parentPaperId == null || t.parentPaperId!.isEmpty || t.parentPaperId == parent) &&
            t.deletedAt >= timestamp);
    int max(int a, int b) => a > b ? a : b;
    final locals = {for (final p in local.papers) p.id: p};
    final remotes = {for (final p in remote.papers) p.id: p};
    final papers = <PaperData>[];
    for (final id in {...locals.keys, ...remotes.keys}) {
      final a = locals[id];
      final b = remotes[id];
      if (deleted('paper', id, null, max(a?.updatedAt ?? 0, b?.updatedAt ?? 0))) continue;
      final fields = a != null && b != null
          ? SyncJson.mergeFields(SyncJson.paper(a, includeItems: false),
              SyncJson.paper(b, includeItems: false), a.updatedAt, b.updatedAt)
          : SyncJson.paper(a ?? b!, includeItems: false);
      final p = PaperData.fromJson({...SyncJson.cloneMap((a ?? b!).toJson()), ...fields});
      p.isDeleted = false;
      final ai = {for (final i in a?.items ?? <PaperItem>[]) i.id: i};
      final bi = {for (final i in b?.items ?? <PaperItem>[]) i.id: i};
      final items = <PaperItem>[];
      for (final itemId in {...ai.keys, ...bi.keys}) {
        final x = ai[itemId];
        final y = bi[itemId];
        if (deleted('item', itemId, id, max(x?.updatedAt ?? 0, y?.updatedAt ?? 0))) continue;
        final item = PaperItem.fromJson(x != null && y != null
            ? SyncJson.mergeFields(SyncJson.item(x), SyncJson.item(y), x.updatedAt, y.updatedAt)
            : SyncJson.cloneMap((x ?? y!).toJson()));
        item.isDeleted = false;
        items.add(item);
      }
      items.sort((x, y) {
        final order = x.order.compareTo(y.order);
        return order != 0 ? order : x.id.compareTo(y.id);
      });
      p.items = items;
      papers.add(p);
    }
    final merged = AppState.fromJson(SyncJson.cloneMap(local.toJson()))
      ..papers = papers
      ..syncTombstones = tombstones.values.toList();
    final content = SyncJson.canonical(SyncJson.document(merged));
    return DataMergeResult(
      mergedState: merged,
      hasChangesToLocal: content != SyncJson.canonical(SyncJson.document(local)),
      hasChangesToRemote: content != SyncJson.canonical(SyncJson.document(remote)),
    );
  }
}

class SyncChangeTracker {
  Map<String, ({String content, int time, String? parent})> _baseline = {};
  Map<String, ({String content, int time, String? parent})> _snapshot(AppState state) {
    String fingerprint(Map<String, dynamic> map) {
      map.remove('updatedAt');
      return SyncJson.canonical(map);
    }
    return {
      for (final p in state.papers) ...{
        'paper:${p.id}': (content: fingerprint(SyncJson.paper(p, includeItems: false)), time: p.updatedAt, parent: null),
        for (final i in p.items)
          'item:${i.id}': (content: fingerprint(SyncJson.item(i)), time: i.updatedAt, parent: p.id),
      },
    };
  }
  void capture(AppState state) => _baseline = _snapshot(state);
  void update(AppState state) {
    final snapshot = _snapshot(state);
    final now = DateTime.now().millisecondsSinceEpoch;
    int next(int previous, int current) {
      final floor = (previous > current ? previous : current) + 1;
      return now > floor ? now : floor;
    }
    for (final p in state.papers) {
      final old = _baseline['paper:${p.id}'];
      if (old != null && old.content != snapshot['paper:${p.id}']!.content) p.updatedAt = next(old.time, p.updatedAt);
      for (final i in p.items) {
        final old = _baseline['item:${i.id}'];
        if (old != null && old.content != snapshot['item:${i.id}']!.content) i.updatedAt = next(old.time, i.updatedAt);
      }
    }
    for (final entry in _baseline.entries.where((e) => !snapshot.containsKey(e.key))) {
      final split = entry.key.indexOf(':');
      final type = entry.key.substring(0, split);
      final id = entry.key.substring(split + 1);
      final matches = state.syncTombstones.where((t) => t.id == id && t.type == type && t.parentPaperId == entry.value.parent);
      if (matches.isEmpty) {
        state.syncTombstones.add(SyncTombstone(id: id, type: type, parentPaperId: entry.value.parent,
            deletedAt: next(entry.value.time, entry.value.time)));
      } else {
        matches.first.deletedAt = next(entry.value.time, matches.first.deletedAt);
      }
    }
    capture(state);
  }
}

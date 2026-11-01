export 'sync_protocol.dart';
import 'package:uuid/uuid.dart';

class PaperItem {
  Map<String, dynamic> extraFields = {};
  Set<String>? presentSyncFields;
  List<double> todoColumnWidths;
  String id;
  String text;
  bool done;
  int order;
  int updatedAt;
  bool isDeleted;
  String? linkedNoteId;
  String? dueAtLocal;
  int todoColumnCount;
  List<String> todoExtraColumns;
  int? reminderIntervalValue;
  String? reminderIntervalUnit;

  PaperItem({
    String? id,
    this.text = '',
    this.done = false,
    this.order = 0,
    int? updatedAt,
    this.isDeleted = false,
    this.linkedNoteId,
    this.dueAtLocal,
    this.todoColumnCount = 1,
    List<String>? todoExtraColumns,
    List<double>? todoColumnWidths,
    this.reminderIntervalValue,
    this.reminderIntervalUnit,
  })  : id = id ?? const Uuid().v4().replaceAll('-', ''),
        todoExtraColumns = todoExtraColumns ?? [],
        todoColumnWidths = todoColumnWidths ?? [],
        updatedAt = updatedAt ?? DateTime.now().millisecondsSinceEpoch;

  factory PaperItem.fromJson(Map<String, dynamic> json) {
    final extraCols = (json['todoExtraColumns'] as List<dynamic>?)
            ?.map((e) => e.toString())
            .toList() ??
        [];
    return PaperItem(
      id: json['id'] as String? ?? const Uuid().v4().replaceAll('-', ''),
      text: json['text'] as String? ?? '',
      done: json['done'] as bool? ?? false,
      order: json['order'] as int? ?? 0,
      updatedAt: json['updatedAt'] as int? ?? DateTime.now().millisecondsSinceEpoch,
      isDeleted: json['isDeleted'] as bool? ?? false,
      linkedNoteId: json['linkedNoteId'] as String?,
      dueAtLocal: json['dueAtLocal'] as String?,
      todoColumnCount: json['todoColumnCount'] as int? ?? 1,
      todoExtraColumns: extraCols,
      todoColumnWidths: (json['todoColumnWidths'] as List?)?.map((e) => (e as num).toDouble()).toList(),
      reminderIntervalValue: json['reminderIntervalValue'] as int?,
      reminderIntervalUnit: json['reminderIntervalUnit'] as String?,
    )..extraFields = Map<String, dynamic>.from(json);
  }

  Map<String, dynamic> toJson() {
    final map = <String, dynamic>{
      ...extraFields,
      'todoColumnWidths': todoColumnWidths,
      'linkedNoteId': linkedNoteId,
      'dueAtLocal': dueAtLocal,
      'reminderIntervalValue': reminderIntervalValue,
      'reminderIntervalUnit': reminderIntervalUnit,
      'isDeleted': isDeleted,
      'id': id,
      'text': text,
      'done': done,
      'order': order,
      'updatedAt': updatedAt,
      'todoColumnCount': todoColumnCount,
      'todoExtraColumns': todoExtraColumns,
    };
    if (isDeleted) map['isDeleted'] = true;
    if (linkedNoteId != null) map['linkedNoteId'] = linkedNoteId;
    if (dueAtLocal != null) map['dueAtLocal'] = dueAtLocal;
    if (reminderIntervalValue != null) map['reminderIntervalValue'] = reminderIntervalValue;
    if (reminderIntervalUnit != null) map['reminderIntervalUnit'] = reminderIntervalUnit;
    return map;
  }
}

class PaperData {
  Map<String, dynamic> extraFields = {};
  Set<String>? presentSyncFields;
  List<dynamic> noteCanvasElements;
  String id;
  String type; // "todo" or "note"
  String title;
  String content;
  List<PaperItem> items;
  int updatedAt;
  bool isDeleted;
  bool isVisible;
  bool isCollapsed;

  PaperData({
    String? id,
    this.type = 'todo',
    this.title = '',
    this.content = '',
    List<PaperItem>? items,
    List<dynamic>? noteCanvasElements,
    int? updatedAt,
    this.isDeleted = false,
    this.isVisible = true,
    this.isCollapsed = false,
  })  : id = id ?? const Uuid().v4().replaceAll('-', ''),
        items = items ?? [],
        noteCanvasElements = noteCanvasElements ?? [],
        updatedAt = updatedAt ?? DateTime.now().millisecondsSinceEpoch;

  factory PaperData.fromJson(Map<String, dynamic> json) {
    final itemsList = (json['items'] as List<dynamic>?)
            ?.map((i) => PaperItem.fromJson(i as Map<String, dynamic>))
            .toList() ??
        [];
    return PaperData(
      id: json['id'] as String? ?? const Uuid().v4().replaceAll('-', ''),
      type: json['type'] as String? ?? 'todo',
      title: json['title'] as String? ?? '',
      content: json['content'] as String? ?? '',
      items: itemsList,
      noteCanvasElements: json['noteCanvasElements'] as List<dynamic>?,
      updatedAt: json['updatedAt'] as int? ?? DateTime.now().millisecondsSinceEpoch,
      isDeleted: json['isDeleted'] as bool? ?? false,
      isVisible: json['isVisible'] as bool? ?? true,
      isCollapsed: json['isCollapsed'] as bool? ?? false,
    )..extraFields = Map<String, dynamic>.from(json);
  }

  Map<String, dynamic> toJson() {
    final map = <String, dynamic>{
      ...extraFields,
      'noteCanvasElements': noteCanvasElements,
      'isDeleted': isDeleted,
      'id': id,
      'type': type,
      'title': title,
      'content': content,
      'items': items.map((i) => i.toJson()).toList(),
      'updatedAt': updatedAt,
      'isVisible': isVisible,
      'isCollapsed': isCollapsed,
    };
    if (isDeleted) map['isDeleted'] = true;
    return map;
  }
}

class SyncTombstone {
  String id;
  String type; // "paper" or "item"
  String? parentPaperId;
  int deletedAt;

  SyncTombstone({
    required this.id,
    this.type = 'item',
    this.parentPaperId,
    int? deletedAt,
  }) : deletedAt = deletedAt ?? DateTime.now().millisecondsSinceEpoch;

  factory SyncTombstone.fromJson(Map<String, dynamic> json) {
    return SyncTombstone(
      id: json['id'] as String? ?? '',
      type: json['type'] as String? ?? 'item',
      parentPaperId: json['parentPaperId'] as String?,
      deletedAt: json['deletedAt'] as int? ?? DateTime.now().millisecondsSinceEpoch,
    );
  }

  Map<String, dynamic> toJson() {
    final map = <String, dynamic>{
      'id': id,
      'type': type,
      'deletedAt': deletedAt,
    };
    if (parentPaperId != null) map['parentPaperId'] = parentPaperId;
    return map;
  }
}

class AppState {
  Map<String, dynamic> extraFields = {};
  String? recoveryMessage;
  List<PaperData> papers;
  List<SyncTombstone> syncTombstones;

  // Sync settings
  bool syncEnabled;
  String syncProvider; // "webdav", "s3", "custom"
  String syncWebDavUrl;
  String syncWebDavUsername;
  String syncWebDavPassword;
  String syncWebDavRemotePath;
  String syncS3Endpoint;
  String syncS3Bucket;
  String syncS3AccessKey;
  String syncS3SecretKey;
  String syncS3Region;
  String syncCustomServerUrl;
  String syncCustomToken;
  int lastSyncTimeUtc;
  String lastSyncStatus;
  String mobileViewMode; // 'bookmark' or 'flow'
  bool notificationsEnabled;
  int notificationLeadMinutes;
  bool notifyOverdue;

  AppState({
    List<PaperData>? papers,
    List<SyncTombstone>? syncTombstones,
    this.syncEnabled = false,
    this.syncProvider = 'webdav',
    this.syncWebDavUrl = '',
    this.syncWebDavUsername = '',
    this.syncWebDavPassword = '',
    this.syncWebDavRemotePath = '/papertodo/data.json',
    this.syncS3Endpoint = '',
    this.syncS3Bucket = '',
    this.syncS3AccessKey = '',
    this.syncS3SecretKey = '',
    this.syncS3Region = 'us-east-1',
    this.syncCustomServerUrl = '',
    this.syncCustomToken = '',
    this.lastSyncTimeUtc = 0,
    this.lastSyncStatus = '',
    this.mobileViewMode = 'bookmark',
    this.notificationsEnabled = true,
    this.notificationLeadMinutes = 15,
    this.notifyOverdue = true,
  })  : papers = papers ?? [],
        syncTombstones = syncTombstones ?? [];

  void recordDeletedItem(String itemId, [String? parentPaperId]) {
    if (itemId.isEmpty) return;
    syncTombstones.removeWhere((t) => t.id == itemId);
    syncTombstones.add(SyncTombstone(
      id: itemId,
      type: 'item',
      parentPaperId: parentPaperId,
    ));
  }

  void recordDeletedPaper(String paperId) {
    if (paperId.isEmpty) return;
    syncTombstones.removeWhere((t) => t.id == paperId);
    syncTombstones.add(SyncTombstone(
      id: paperId,
      type: 'paper',
    ));
  }

  factory AppState.fromJson(Map<String, dynamic> json) {
    final papersList = (json['papers'] as List<dynamic>?)
            ?.map((p) => PaperData.fromJson(p as Map<String, dynamic>))
            .toList() ??
        [];
    final tombstonesList = (json['syncTombstones'] as List<dynamic>?)
            ?.map((t) => SyncTombstone.fromJson(t as Map<String, dynamic>))
            .toList() ??
        [];

    return AppState(
      papers: papersList,
      syncTombstones: tombstonesList,
      syncEnabled: json['syncEnabled'] as bool? ?? false,
      syncProvider: json['syncProvider'] as String? ?? 'webdav',
      syncWebDavUrl: json['syncWebDavUrl'] as String? ?? '',
      syncWebDavUsername: json['syncWebDavUsername'] as String? ?? '',
      syncWebDavPassword: json['syncWebDavPassword'] as String? ?? '',
      syncWebDavRemotePath: json['syncWebDavRemotePath'] as String? ?? '/papertodo/data.json',
      syncS3Endpoint: json['syncS3Endpoint'] as String? ?? '',
      syncS3Bucket: json['syncS3Bucket'] as String? ?? '',
      syncS3AccessKey: json['syncS3AccessKey'] as String? ?? '',
      syncS3SecretKey: json['syncS3SecretKey'] as String? ?? '',
      syncS3Region: json['syncS3Region'] as String? ?? 'us-east-1',
      syncCustomServerUrl: json['syncCustomServerUrl'] as String? ?? '',
      syncCustomToken: json['syncCustomToken'] as String? ?? '',
      lastSyncTimeUtc: json['lastSyncTimeUtc'] as int? ?? 0,
      lastSyncStatus: json['lastSyncStatus'] as String? ?? '',
      mobileViewMode: json['mobileViewMode'] as String? ?? 'bookmark',
      notificationsEnabled: json['notificationsEnabled'] as bool? ?? true,
      notificationLeadMinutes: json['notificationLeadMinutes'] as int? ?? 15,
      notifyOverdue: json['notifyOverdue'] as bool? ?? true,
    )..extraFields = Map<String, dynamic>.from(json);
  }

  Map<String, dynamic> toJson() {
    return {
      ...extraFields,
      'papers': papers.map((p) => p.toJson()).toList(),
      'syncTombstones': syncTombstones.map((t) => t.toJson()).toList(),
      'syncEnabled': syncEnabled,
      'syncProvider': syncProvider,
      'syncWebDavUrl': syncWebDavUrl,
      'syncWebDavUsername': syncWebDavUsername,
      'syncWebDavPassword': syncWebDavPassword,
      'syncWebDavRemotePath': syncWebDavRemotePath,
      'syncS3Endpoint': syncS3Endpoint,
      'syncS3Bucket': syncS3Bucket,
      'syncS3AccessKey': syncS3AccessKey,
      'syncS3SecretKey': syncS3SecretKey,
      'syncS3Region': syncS3Region,
      'syncCustomServerUrl': syncCustomServerUrl,
      'syncCustomToken': syncCustomToken,
      'lastSyncTimeUtc': lastSyncTimeUtc,
      'lastSyncStatus': lastSyncStatus,
      'mobileViewMode': mobileViewMode,
      'notificationsEnabled': notificationsEnabled,
      'notificationLeadMinutes': notificationLeadMinutes,
      'notifyOverdue': notifyOverdue,
    };
  }
}

class DataMergeResult {
  final AppState mergedState;
  final bool hasChangesToLocal;
  final bool hasChangesToRemote;

  DataMergeResult({
    required this.mergedState,
    required this.hasChangesToLocal,
    required this.hasChangesToRemote,
  });
}

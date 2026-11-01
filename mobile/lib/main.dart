import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart';
import 'models.dart';
import 'notification_service.dart';
import 'sync_service.dart';
import 'companion_service.dart';
import 'background_sync.dart' as background;
import 'backup_transfer.dart';
import 'due_date_sheet.dart';

@pragma('vm:entry-point')
Future<void> backgroundSync() => background.runBackgroundSync();

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const PaperTodoApp());
}

class MiuixColors {
  static const Color primary = Color(0xFF3482FF);       // HyperOS Electric Blue
  static const Color primaryVariant = Color(0xFF5D9BFF);
  static const Color primaryContainer = Color(0xFFEAF2FF);
  static const Color onPrimaryContainer = Color(0xFF3482FF);
  static const Color background = Color(0xFFF7F7F7);    // HyperOS Surface
  static const Color card = Color(0xFFFFFFFF);          // HyperOS SuperCard
  static const Color textPrimary = Color(0xFF111111);
  static const Color textSecondary = Color(0xFF8E8E93);
  static const Color divider = Color(0xFFE5E5EA);
  static const Color error = Color(0xFFE94634);          // HyperOS Alert Red
  static const Color errorContainer = Color(0xFFFDF2F2);
  static const Color tagBg = Color(0xFFF2F2F7);
}

class PaperTodoApp extends StatelessWidget {
  const PaperTodoApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'PaperTodo',
      debugShowCheckedModeBanner: false,
      supportedLocales: const [Locale('zh', 'CN')],
      localizationsDelegates: const [
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      theme: ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(
          seedColor: MiuixColors.primary,
          primary: MiuixColors.primary,
          surface: MiuixColors.background,
          brightness: Brightness.light,
        ),
        scaffoldBackgroundColor: MiuixColors.background,
        cardTheme: CardThemeData(
          color: MiuixColors.card,
          elevation: 0,
          margin: EdgeInsets.zero,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(18),
            side: const BorderSide(color: Color(0xFFE5E5EA), width: 0.6),
          ),
        ),
        appBarTheme: const AppBarTheme(
          backgroundColor: MiuixColors.background,
          elevation: 0,
          scrolledUnderElevation: 0,
          surfaceTintColor: Colors.transparent,
          iconTheme: IconThemeData(color: MiuixColors.textPrimary),
          titleTextStyle: TextStyle(
            color: MiuixColors.textPrimary,
            fontSize: 20,
            fontWeight: FontWeight.w800,
            letterSpacing: -0.4,
          ),
        ),
        dividerColor: MiuixColors.divider,
      ),
      home: const HomeScreen(),
    );
  }
}

class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> with SingleTickerProviderStateMixin, WidgetsBindingObserver {
  late TabController _tabController;
  AppState _state = AppState();
  bool _isLoading = true;
  bool _isSyncing = false;
  int _selectedPaperIndex = 0;
  Timer? _notificationTimer;
  Timer? _debounceSyncTimer;
  Timer? _pullTimer;
  String? _loadError;
  bool _syncAgain = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    CompanionService.listen(_consumeWidgetActions);
    _tabController = TabController(length: 3, vsync: this);
    _loadDataAndAutoSync();
    NotificationService.requestPermission();
    _notificationTimer = Timer.periodic(const Duration(minutes: 1), (_) {
      NotificationService.checkDueReminders(_state);
    });
    _pullTimer = Timer.periodic(const Duration(seconds: 30), (_) {
      if (!_isLoading && _loadError == null && _state.syncEnabled &&
          WidgetsBinding.instance.lifecycleState == AppLifecycleState.resumed) {
        _doSync(silent: true);
      }
    });
  }

  @override
  void dispose() {
    _tabController.dispose();
    _notificationTimer?.cancel();
    _debounceSyncTimer?.cancel();
    _pullTimer?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    CompanionService.listen(null);
    super.dispose();
  }

  Future<void> _loadDataAndAutoSync() async {
    try {
      final state = await SyncManager.loadLocalState();
      await CompanionService.applyPendingActions(state);
      if (!mounted) return;
      setState(() { _state = state; _isLoading = false; _loadError = null; });
      if (state.recoveryMessage != null) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(state.recoveryMessage!)));
      }
      NotificationService.checkDueReminders(_state);
      if (_state.syncEnabled) _doSync(silent: true);
    } catch (error) {
      if (mounted) setState(() { _loadError = error.toString(); _isLoading = false; });
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState lifecycle) {
    if (lifecycle == AppLifecycleState.resumed && !_isLoading && _loadError == null) {
      _resume();
    }
  }

  Future<void> _resume() async {
    try {
      await SyncManager.refreshLocalState(_state);
      await CompanionService.applyPendingActions(_state);
      if (!mounted) return;
      setState(() {});
      NotificationService.checkDueReminders(_state);
      if (_state.syncEnabled) _doSync(silent: true);
    } catch (error) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(error.toString())));
    }
  }

  Future<void> _consumeWidgetActions() async {
    if (_isLoading || _loadError != null) return;
    try {
      await CompanionService.applyPendingActions(_state);
      if (!mounted) return;
      setState(() {});
      await CompanionService.update(_state);
      if (_state.syncEnabled) _doSync(silent: true);
    } catch (error) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(error.toString())));
    }
  }

  Future<void> _restoreBackup() async {
    try {
      final contents = await BackupTransfer.readBackup();
      if (contents == null || !mounted) return;
      final preview = SyncJson.parse(contents);
      final confirmed = await showDialog<bool>(context: context, builder: (context) => AlertDialog(
        title: const Text('从备份恢复纸片'),
        content: Text('将恢复 ${preview.papers.length} 张纸片。当前原文件会单独保留，同步连接需要重新配置。'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('取消')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('恢复')),
        ],
      ));
      if (confirmed != true) return;
      await SyncManager.restoreLocalState(contents);
      if (!mounted) return;
      setState(() { _loadError = null; _isLoading = true; });
      await _loadDataAndAutoSync();
    } catch (error) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('恢复未完成，原文件已保留：$error')));
    }
  }

  Future<void> _saveAndNotify() async {
    try {
      await SyncManager.saveLocalState(_state);
    } catch (error) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('保存失败，当前更改仍保留在界面中：$error')));
      return;
    }
    if (!mounted) return;
    setState(() {});
    NotificationService.checkDueReminders(_state);
    if (_state.syncEnabled) {
      _debounceSyncTimer?.cancel();
      _debounceSyncTimer = Timer(const Duration(seconds: 3), () {
        if (mounted) {
          _doSync(silent: true);
        }
      });
    }
  }

  Future<void> _doSync({bool silent = false}) async {
    if (_isSyncing) { _syncAgain = true; return; }
    setState(() => _isSyncing = true);

    final res = await SyncManager.sync(_state);
    if (mounted) {
      setState(() => _isSyncing = false);
      NotificationService.checkDueReminders(_state);
      if (!silent) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(res.message),
            backgroundColor: res.success ? Colors.green.shade700 : Colors.red.shade700,
            duration: const Duration(seconds: 2),
          ),
        );
      }
      if (_syncAgain && _state.syncEnabled) {
        _syncAgain = false;
        _debounceSyncTimer?.cancel();
        _debounceSyncTimer = Timer(const Duration(seconds: 3), () {
          if (mounted) _doSync(silent: true);
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loadError != null) {
      return Scaffold(
        appBar: AppBar(title: const Text('PaperTodo')),
        body: Center(child: Padding(padding: const EdgeInsets.all(24), child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.error_outline, size: 40),
            const SizedBox(height: 16),
            const Text('本地数据暂时无法读取', style: TextStyle(fontSize: 20)),
            const SizedBox(height: 12),
            Text(_loadError!, textAlign: TextAlign.center),
            const SizedBox(height: 20),
            FilledButton(onPressed: () {
              setState(() { _loadError = null; _isLoading = true; });
              _loadDataAndAutoSync();
            }, child: const Text('重试')),
            TextButton(onPressed: _restoreBackup, child: const Text('从纸片备份恢复')),
          ],
        ))),
      );
    }
    if (_isLoading) {
      return const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      );
    }

    final isBookmarkMode = _state.mobileViewMode == 'bookmark';

    return Scaffold(
      backgroundColor: MiuixColors.background,
      appBar: AppBar(
        titleSpacing: isBookmarkMode ? 4 : 16,
        backgroundColor: MiuixColors.background,
        elevation: 0,
        scrolledUnderElevation: 0,
        leading: isBookmarkMode
            ? Container(
                margin: const EdgeInsets.only(left: 12),
                alignment: Alignment.centerLeft,
                child: Material(
                  color: MiuixColors.primaryContainer,
                  borderRadius: BorderRadius.circular(10),
                  child: InkWell(
                    borderRadius: BorderRadius.circular(10),
                    onTap: () => _openBookmarkDrawer(context),
                    child: const Padding(
                      padding: EdgeInsets.all(7),
                      child: Icon(Icons.bookmarks_rounded, color: MiuixColors.primary, size: 20),
                    ),
                  ),
                ),
              )
            : Container(
                margin: const EdgeInsets.only(left: 14),
                width: 32,
                height: 32,
                decoration: BoxDecoration(
                  color: MiuixColors.primary,
                  borderRadius: BorderRadius.circular(9),
                ),
                alignment: Alignment.center,
                child: const Text(
                  'PT',
                  style: TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 14),
                ),
              ),
        title: isBookmarkMode
            ? InkWell(
                onTap: () => _openBookmarkDrawer(context),
                borderRadius: BorderRadius.circular(8),
                child: const Padding(
                  padding: EdgeInsets.symmetric(horizontal: 4, vertical: 4),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(
                        'PaperTodo',
                        style: TextStyle(fontWeight: FontWeight.w800, fontSize: 20, letterSpacing: -0.4, color: MiuixColors.textPrimary),
                      ),
                      SizedBox(width: 4),
                      Icon(Icons.keyboard_arrow_down_rounded, color: MiuixColors.textSecondary, size: 20),
                    ],
                  ),
                ),
              )
            : const Text(
                'PaperTodo',
                style: TextStyle(fontWeight: FontWeight.w800, fontSize: 20, letterSpacing: -0.4, color: MiuixColors.textPrimary),
              ),
        actions: [
          IconButton(
            tooltip: '同步',
            icon: _isSyncing
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2, color: MiuixColors.primary),
                  )
                : const Icon(Icons.sync_rounded, color: MiuixColors.textPrimary),
            onPressed: () => _doSync(silent: false),
          ),
          IconButton(
            tooltip: '设置',
            icon: const Icon(Icons.settings_outlined, color: MiuixColors.textPrimary),
            onPressed: () async {
              await Navigator.push(
                context,
                MaterialPageRoute(builder: (_) => SettingsScreen(state: _state)),
              );
              if (!mounted) return;
              setState(() {});
              if (_state.syncEnabled) {
                _doSync(silent: false);
              }
            },
          ),
          const SizedBox(width: 6),
        ],
        bottom: PreferredSize(
          preferredSize: const Size.fromHeight(48),
          child: Container(
            margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
            padding: const EdgeInsets.all(3),
            decoration: BoxDecoration(
              color: const Color(0xFFEBEBF0),
              borderRadius: BorderRadius.circular(14),
            ),
            child: TabBar(
              controller: _tabController,
              dividerColor: Colors.transparent,
              indicatorSize: TabBarIndicatorSize.tab,
              indicator: BoxDecoration(
                color: Colors.white,
                borderRadius: BorderRadius.circular(11),
                boxShadow: [
                  BoxShadow(
                    color: Colors.black.withValues(alpha: 0.06),
                    blurRadius: 3,
                    offset: const Offset(0, 1),
                  ),
                ],
              ),
              labelColor: MiuixColors.textPrimary,
              unselectedLabelColor: MiuixColors.textSecondary,
              labelStyle: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5),
              unselectedLabelStyle: const TextStyle(fontWeight: FontWeight.w500, fontSize: 13.5),
              tabs: const [
                Tab(height: 36, text: '全部'),
                Tab(height: 36, text: '待办纸片'),
                Tab(height: 36, text: '便签笔记'),
              ],
            ),
          ),
        ),
      ),
      body: TabBarView(
        controller: _tabController,
        children: [
          _buildContent(null),
          _buildContent('todo'),
          _buildContent('note'),
        ],
      ),
      floatingActionButton: FloatingActionButton(
        backgroundColor: MiuixColors.primary,
        foregroundColor: Colors.white,
        elevation: 2.5,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        onPressed: _showCreatePaperDialog,
        child: const Icon(Icons.add_rounded, size: 28),
      ),
    );
  }

  Widget _buildContent(String? filterType) {
    if (_state.mobileViewMode == 'bookmark') {
      return _buildPaperBookmarkView(filterType);
    } else {
      return _buildPaperList(filterType);
    }
  }

  void _openBookmarkDrawer(BuildContext context) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (ctx) {
        return Container(
          height: MediaQuery.of(ctx).size.height * 0.72,
          decoration: const BoxDecoration(
            color: MiuixColors.background,
            borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
          ),
          child: Column(
            children: [
              Center(
                child: Container(
                  width: 40,
                  height: 4,
                  margin: const EdgeInsets.only(top: 10, bottom: 8),
                  decoration: BoxDecoration(
                    color: const Color(0xFFD1D1D6),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
              ),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 8),
                child: Row(
                  children: [
                    const Icon(Icons.collections_bookmark_rounded, color: MiuixColors.primary, size: 22),
                    const SizedBox(width: 8),
                    Text(
                      '便签书签目录 (${_state.papers.length})',
                      style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 17, color: MiuixColors.textPrimary),
                    ),
                    const Spacer(),
                    IconButton(
                      icon: const Icon(Icons.add_circle_outline, color: MiuixColors.primary),
                      tooltip: '新建便签',
                      onPressed: () {
                        Navigator.pop(ctx);
                        _showCreatePaperDialog();
                      },
                    ),
                    IconButton(
                      icon: const Icon(Icons.close, color: MiuixColors.textSecondary),
                      onPressed: () => Navigator.pop(ctx),
                    ),
                  ],
                ),
              ),
              const Divider(height: 1, color: MiuixColors.divider),
              Expanded(
                child: ListView.separated(
                  padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
                  itemCount: _state.papers.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 8),
                  itemBuilder: (context, index) {
                    final p = _state.papers[index];
                    final isSelected = index == _selectedPaperIndex;
                    final isTodo = p.type == 'todo';
                    final doneCount = p.items.where((i) => i.done).length;
                    final totalCount = p.items.length;

                    return InkWell(
                      onTap: () {
                        setState(() {
                          _selectedPaperIndex = index;
                        });
                        Navigator.pop(ctx);
                      },
                      borderRadius: BorderRadius.circular(16),
                      child: Container(
                        padding: const EdgeInsets.all(13),
                        decoration: BoxDecoration(
                          color: isSelected ? MiuixColors.primaryContainer : Colors.white,
                          borderRadius: BorderRadius.circular(16),
                          border: Border.all(
                            color: isSelected ? MiuixColors.primary : MiuixColors.divider,
                            width: isSelected ? 1.4 : 0.6,
                          ),
                        ),
                        child: Row(
                          children: [
                            Container(
                              width: 36,
                              height: 36,
                              decoration: BoxDecoration(
                                color: isSelected ? MiuixColors.primary : const Color(0xFFF2F2F7),
                                borderRadius: BorderRadius.circular(10),
                              ),
                              child: Icon(
                                isTodo ? Icons.check_box_outlined : Icons.edit_note_rounded,
                                color: isSelected ? Colors.white : MiuixColors.primary,
                                size: 20,
                              ),
                            ),
                            const SizedBox(width: 12),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(
                                    p.title.isEmpty ? (isTodo ? '待办清单' : '便签') : p.title,
                                    style: TextStyle(
                                      fontWeight: isSelected ? FontWeight.w800 : FontWeight.w600,
                                      fontSize: 15.5,
                                      color: isSelected ? MiuixColors.primary : MiuixColors.textPrimary,
                                    ),
                                  ),
                                  const SizedBox(height: 2),
                                  Text(
                                    isTodo ? '完成 $doneCount / $totalCount 项' : (p.content.isEmpty ? '（空笔记）' : p.content),
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                    style: TextStyle(
                                      fontSize: 12.5,
                                      color: isSelected ? MiuixColors.primary.withValues(alpha: 0.8) : MiuixColors.textSecondary,
                                    ),
                                  ),
                                ],
                              ),
                            ),
                            if (isSelected)
                              Container(
                                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                                decoration: BoxDecoration(
                                  color: MiuixColors.primary,
                                  borderRadius: BorderRadius.circular(8),
                                ),
                                child: const Text(
                                  '当前',
                                  style: TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.bold),
                                ),
                              ),
                          ],
                        ),
                      ),
                    );
                  },
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  Widget _buildPaperBookmarkView(String? filterType) {
    final papers = _state.papers.where((p) {
      if (filterType != null && p.type != filterType) return false;
      return true;
    }).toList();

    if (papers.isEmpty) {
      return Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(Icons.bookmarks_outlined, size: 64, color: Colors.grey.shade400),
            const SizedBox(height: 12),
            Text(
              filterType == 'note' ? '暂无便签笔记' : '暂无待办纸片',
              style: TextStyle(color: Colors.grey.shade600, fontSize: 16),
            ),
          ],
        ),
      );
    }

    final currentIndex = _selectedPaperIndex.clamp(0, papers.length - 1);
    final activePaper = papers[currentIndex];

    return Column(
      children: [
        // Top bookmark index bar
        Container(
          margin: const EdgeInsets.fromLTRB(16, 8, 16, 10),
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
          decoration: BoxDecoration(
            color: Colors.white,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: MiuixColors.divider, width: 0.6),
          ),
          child: Row(
            children: [
              IconButton(
                icon: const Icon(Icons.arrow_back_ios_rounded, size: 15, color: MiuixColors.textSecondary),
                padding: EdgeInsets.zero,
                constraints: const BoxConstraints(),
                onPressed: currentIndex > 0
                    ? () {
                        setState(() {
                          _selectedPaperIndex = currentIndex - 1;
                        });
                      }
                    : null,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: InkWell(
                  onTap: () => _openBookmarkDrawer(context),
                  borderRadius: BorderRadius.circular(8),
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      const Icon(Icons.bookmark_rounded, size: 16, color: MiuixColors.primary),
                      const SizedBox(width: 6),
                      Flexible(
                        child: Text(
                          activePaper.title.isEmpty ? (activePaper.type == 'todo' ? '待办清单' : '便签') : activePaper.title,
                          style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14.5, color: MiuixColors.textPrimary),
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      const SizedBox(width: 4),
                      Text(
                        '(${currentIndex + 1}/${papers.length})',
                        style: const TextStyle(fontSize: 12, color: MiuixColors.textSecondary, fontWeight: FontWeight.w500),
                      ),
                      const SizedBox(width: 2),
                      const Icon(Icons.keyboard_arrow_down_rounded, size: 18, color: MiuixColors.textSecondary),
                    ],
                  ),
                ),
              ),
              const SizedBox(width: 8),
              IconButton(
                icon: const Icon(Icons.arrow_forward_ios_rounded, size: 15, color: MiuixColors.textSecondary),
                padding: EdgeInsets.zero,
                constraints: const BoxConstraints(),
                onPressed: currentIndex < papers.length - 1
                    ? () {
                        setState(() {
                          _selectedPaperIndex = currentIndex + 1;
                        });
                      }
                    : null,
              ),
            ],
          ),
        ),
        // Active paper card
        Expanded(
          child: SingleChildScrollView(
            padding: const EdgeInsets.fromLTRB(16, 2, 16, 88),
            child: _buildPaperCard(activePaper),
          ),
        ),
      ],
    );
  }

  Widget _buildPaperList(String? filterType) {
    final papers = _state.papers.where((p) {
      if (filterType != null && p.type != filterType) return false;
      return true;
    }).toList();

    if (papers.isEmpty) {
      return Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(Icons.note_alt_outlined, size: 64, color: Colors.grey.shade400),
            const SizedBox(height: 12),
            Text(
              filterType == 'note' ? '暂无便签笔记' : '暂无待办纸片',
              style: TextStyle(color: Colors.grey.shade600, fontSize: 16),
            ),
          ],
        ),
      );
    }

    return ListView.builder(
      padding: const EdgeInsets.fromLTRB(14, 12, 14, 88),
      itemCount: papers.length,
      itemBuilder: (context, index) {
        final paper = papers[index];
        return _buildPaperCard(paper);
      },
    );
  }

  Widget _buildPaperCard(PaperData paper) {
    final isTodo = paper.type == 'todo';

    return Container(
      margin: const EdgeInsets.only(bottom: 14),
      decoration: BoxDecoration(
        color: MiuixColors.card,
        borderRadius: BorderRadius.circular(18),
        border: Border.all(color: MiuixColors.divider, width: 0.6),
      ),
      child: Padding(
        padding: const EdgeInsets.all(15),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  width: 32,
                  height: 32,
                  decoration: BoxDecoration(
                    color: MiuixColors.primaryContainer,
                    borderRadius: BorderRadius.circular(9),
                  ),
                  child: Icon(
                    isTodo ? Icons.check_box_outlined : Icons.edit_note_rounded,
                    size: 18,
                    color: MiuixColors.primary,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    paper.title.isEmpty ? (isTodo ? '待办清单' : '便签') : paper.title,
                    style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 16.5, color: MiuixColors.textPrimary),
                  ),
                ),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                  decoration: BoxDecoration(
                    color: MiuixColors.tagBg,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    isTodo ? '${paper.items.length} 项' : '便签',
                    style: const TextStyle(fontSize: 11.5, color: MiuixColors.textSecondary, fontWeight: FontWeight.w600),
                  ),
                ),
                const SizedBox(width: 4),
                PopupMenuButton<String>(
                  icon: const Icon(Icons.more_horiz, size: 20, color: MiuixColors.textSecondary),
                  onSelected: (val) {
                    if (val == 'delete') {
                      _deletePaper(paper);
                    }
                  },
                  itemBuilder: (_) => [
                    const PopupMenuItem(
                      value: 'delete',
                      child: Row(
                        children: [
                          Icon(Icons.delete_outline, size: 18, color: MiuixColors.error),
                          SizedBox(width: 8),
                          Text('删除纸片', style: TextStyle(color: MiuixColors.error)),
                        ],
                      ),
                    ),
                  ],
                ),
              ],
            ),
            const Divider(height: 18, thickness: 0.5, color: MiuixColors.divider),
            if (isTodo) ...[
              ...paper.items.map((item) => _buildTodoRow(paper, item)),
              const SizedBox(height: 8),
              _buildFastAppendRow(paper),
            ] else ...[
              Text(
                paper.content.isEmpty ? '（空笔记）' : paper.content,
                style: const TextStyle(
                  fontSize: 14.5,
                  height: 1.6,
                  color: MiuixColors.textPrimary,
                ),
              ),
              const SizedBox(height: 8),
              Align(
                alignment: Alignment.centerRight,
                child: TextButton.icon(
                  icon: const Icon(Icons.edit, size: 15),
                  label: const Text('编辑内容'),
                  style: TextButton.styleFrom(
                    foregroundColor: MiuixColors.primary,
                  ),
                  onPressed: () => _editNoteContent(paper),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _buildTodoRow(PaperData paper, PaperItem item) {
    final hasDue = item.dueAtLocal != null && item.dueAtLocal!.isNotEmpty;
    final hasLinkedNote = item.linkedNoteId != null && item.linkedNoteId!.isNotEmpty;

    return Dismissible(
      key: ValueKey(item.id),
      direction: DismissDirection.endToStart,
      background: Container(
        color: Colors.red.shade400,
        alignment: Alignment.centerRight,
        padding: const EdgeInsets.only(right: 16),
        child: const Icon(Icons.delete_outline, color: Colors.white),
      ),
      onDismissed: (_) {
        setState(() {
          paper.items.removeWhere((i) => i.id == item.id);
          _state.recordDeletedItem(item.id, paper.id);
        });
        _saveAndNotify();
      },
      child: InkWell(
        onTap: () {
          setState(() {
            item.done = !item.done;
            item.updatedAt = DateTime.now().millisecondsSinceEpoch;
            paper.updatedAt = item.updatedAt;
          });
          _saveAndNotify();
        },
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.only(top: 2),
                child: Checkbox(
                  value: item.done,
                  activeColor: MiuixColors.primary,
                  checkColor: Colors.white,
                  shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6)),
                  side: const BorderSide(color: Color(0xFFC7C7CC), width: 1.5),
                  onChanged: (val) {
                    setState(() {
                      item.done = val ?? false;
                      item.updatedAt = DateTime.now().millisecondsSinceEpoch;
                      paper.updatedAt = item.updatedAt;
                    });
                    _saveAndNotify();
                  },
                ),
              ),
              Expanded(
                child: Padding(
                  padding: const EdgeInsets.only(top: 8, bottom: 6),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        item.text,
                        style: TextStyle(
                          fontSize: 14.5,
                          decoration: item.done ? TextDecoration.lineThrough : null,
                          color: item.done ? MiuixColors.textSecondary : MiuixColors.textPrimary,
                        ),
                      ),
                      if (hasDue || hasLinkedNote || item.todoColumnCount > 1) ...[
                        const SizedBox(height: 5),
                        Wrap(
                          spacing: 6,
                          runSpacing: 4,
                          crossAxisAlignment: WrapCrossAlignment.center,
                          children: [
                            if (hasDue) _buildDueChip(paper, item),
                            if (hasLinkedNote) _buildLinkedNoteChip(item.linkedNoteId!),
                            for (var c = 0; c < item.todoColumnCount - 1; c++)
                              _buildExtraColumnChip(paper, item, c),
                            if (item.todoColumnCount < 4)
                              _buildAddColumnChip(paper, item),
                          ],
                        ),
                      ],
                    ],
                  ),
                ),
              ),
              PopupMenuButton<String>(
                icon: const Icon(Icons.more_horiz, size: 18, color: Colors.black26),
                padding: EdgeInsets.zero,
                constraints: const BoxConstraints(),
                onSelected: (val) {
                  if (val == 'due') {
                    _pickTodoDueDate(paper, item);
                  } else if (val == 'cleardue') {
                    setState(() {
                      item.dueAtLocal = null;
                      item.updatedAt = DateTime.now().millisecondsSinceEpoch;
                      paper.updatedAt = item.updatedAt;
                    });
                    _saveAndNotify();
                  } else if (val == 'add_col') {
                    _increaseColumns(paper, item);
                  } else if (val == 'edit_cols') {
                    _editAllColumnsDialog(paper, item);
                  } else if (val == 'del_col') {
                    _decreaseColumns(paper, item);
                  } else if (val.startsWith('del_col_')) {
                    final colNum = int.tryParse(val.replaceFirst('del_col_', ''));
                    if (colNum != null) {
                      _deleteColumn(paper, item, colNum);
                    }
                  } else if (val == 'delete') {
                    setState(() {
                      paper.items.removeWhere((i) => i.id == item.id);
                      _state.recordDeletedItem(item.id, paper.id);
                    });
                    _saveAndNotify();
                  }
                },
                itemBuilder: (_) => [
                  PopupMenuItem(
                    value: 'due',
                    child: Row(
                      children: [
                        const Icon(Icons.access_time_rounded, size: 18, color: MiuixColors.primary),
                        const SizedBox(width: 8),
                        Text(item.dueAtLocal != null ? '修改时间节点' : '设置时间节点'),
                      ],
                    ),
                  ),
                  if (item.dueAtLocal != null)
                    const PopupMenuItem(
                      value: 'cleardue',
                      child: Row(
                        children: [
                          Icon(Icons.timer_off_outlined, size: 18, color: Colors.black54),
                          SizedBox(width: 8),
                          Text('清除时间节点'),
                        ],
                      ),
                    ),
                  PopupMenuItem(
                    value: 'add_col',
                    enabled: item.todoColumnCount < 4,
                    child: Row(
                      children: [
                        const Icon(Icons.view_column_rounded, size: 18, color: MiuixColors.primary),
                        const SizedBox(width: 8),
                        Text('增加列数 (当前 ${item.todoColumnCount} 列)'),
                      ],
                    ),
                  ),
                  if (item.todoColumnCount > 1)
                    const PopupMenuItem(
                      value: 'edit_cols',
                      child: Row(
                        children: [
                          Icon(Icons.edit_note_rounded, size: 18, color: MiuixColors.primary),
                          SizedBox(width: 8),
                          Text('编辑多列内容'),
                        ],
                      ),
                    ),
                  if (item.todoColumnCount > 1)
                    for (var c = 0; c < item.todoColumnCount - 1; c++)
                      PopupMenuItem(
                        value: 'del_col_${c + 2}',
                        child: Row(
                          children: [
                            const Icon(Icons.delete_sweep_outlined, size: 18, color: MiuixColors.error),
                            const SizedBox(width: 8),
                            Text(
                              '删除第 ${c + 2} 列${item.todoExtraColumns[c].isNotEmpty ? " (${item.todoExtraColumns[c]})" : ""}',
                              style: const TextStyle(color: MiuixColors.error),
                            ),
                          ],
                        ),
                      ),
                  const PopupMenuItem(
                    value: 'delete',
                    child: Row(
                      children: [
                        Icon(Icons.delete_outline, size: 18, color: MiuixColors.error),
                        SizedBox(width: 8),
                        Text('删除待办', style: TextStyle(color: MiuixColors.error)),
                      ],
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  DateTime? _parseDue(String? dueStr) {
    if (dueStr == null || dueStr.isEmpty) return null;
    return DateTime.tryParse(dueStr);
  }

  Widget _buildDueChip(PaperData paper, PaperItem item) {
    final due = _parseDue(item.dueAtLocal);
    if (due == null) return const SizedBox.shrink();

    final now = DateTime.now();
    final isOverdue = !item.done && due.isBefore(now);
    final isToday = isSameCalendarDay(due, now);

    String labelText;
    final timeStr = DateFormat('HH:mm').format(due);
    if (isOverdue) {
      labelText = '已逾期 (${DateFormat('MM-dd HH:mm').format(due)})';
    } else {
      labelText = '${shortDueDate(due, now)} $timeStr';
    }

    final Color bgColor;
    final Color textColor;
    final Color iconColor;

    if (item.done) {
      bgColor = MiuixColors.tagBg;
      textColor = MiuixColors.textSecondary;
      iconColor = MiuixColors.textSecondary;
    } else if (isOverdue) {
      bgColor = MiuixColors.errorContainer;
      textColor = MiuixColors.error;
      iconColor = MiuixColors.error;
    } else if (isToday) {
      bgColor = MiuixColors.primaryContainer;
      textColor = MiuixColors.primary;
      iconColor = MiuixColors.primary;
    } else {
      bgColor = MiuixColors.tagBg;
      textColor = const Color(0xFF48484A);
      iconColor = const Color(0xFF48484A);
    }

    return InkWell(
      onTap: () => _pickTodoDueDate(paper, item),
      borderRadius: BorderRadius.circular(6),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
        decoration: BoxDecoration(
          color: bgColor,
          borderRadius: BorderRadius.circular(6),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              isOverdue ? Icons.error_outline_rounded : Icons.access_time_rounded,
              size: 13,
              color: iconColor,
            ),
            const SizedBox(width: 4),
            Text(
              labelText,
              style: TextStyle(
                fontSize: 11.5,
                fontWeight: FontWeight.w600,
                color: textColor,
                decoration: item.done ? TextDecoration.lineThrough : null,
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildLinkedNoteChip(String noteId) {
    final note = _state.papers.firstWhere(
      (p) => p.id == noteId,
      orElse: () => PaperData(title: '关联便签', content: '暂无笔记内容'),
    );
    return InkWell(
      onTap: () => _showNotePreviewSheet(note),
      borderRadius: BorderRadius.circular(6),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
        decoration: BoxDecoration(
          color: const Color(0xFFE8F5E9),
          borderRadius: BorderRadius.circular(6),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.description_outlined, size: 13, color: Color(0xFF2E7D32)),
            const SizedBox(width: 4),
            Text(
              note.title.isEmpty ? '笔记' : note.title,
              style: const TextStyle(
                fontSize: 11.5,
                fontWeight: FontWeight.w600,
                color: Color(0xFF2E7D32),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildExtraColumnChip(PaperData paper, PaperItem item, int colIdx) {
    final text = (colIdx < item.todoExtraColumns.length) ? item.todoExtraColumns[colIdx] : '';
    return InkWell(
      onTap: () => _editColumnDialog(paper, item, colIdx + 2),
      borderRadius: BorderRadius.circular(6),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
        decoration: BoxDecoration(
          color: MiuixColors.tagBg,
          borderRadius: BorderRadius.circular(6),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              '列 ${colIdx + 2}: ${text.isEmpty ? "（点击输入）" : text}',
              style: TextStyle(
                fontSize: 11.5,
                color: text.isEmpty ? MiuixColors.textSecondary : const Color(0xFF37474F),
                fontWeight: FontWeight.w500,
              ),
            ),
            const SizedBox(width: 3),
            const Icon(Icons.edit, size: 10, color: MiuixColors.textSecondary),
          ],
        ),
      ),
    );
  }

  Widget _buildAddColumnChip(PaperData paper, PaperItem item) {
    return InkWell(
      onTap: () => _increaseColumns(paper, item),
      borderRadius: BorderRadius.circular(6),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
        decoration: BoxDecoration(
          color: Colors.white,
          borderRadius: BorderRadius.circular(6),
          border: Border.all(color: MiuixColors.primary, width: 0.8),
        ),
        child: const Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.add, size: 12, color: MiuixColors.primary),
            SizedBox(width: 2),
            Text(
              '加列',
              style: TextStyle(fontSize: 11, color: MiuixColors.primary, fontWeight: FontWeight.bold),
            ),
          ],
        ),
      ),
    );
  }

  void _increaseColumns(PaperData paper, PaperItem item) {
    final livePaper = _state.papers.firstWhere((p) => p.id == paper.id, orElse: () => paper);
    final liveItem = livePaper.items.firstWhere((i) => i.id == item.id, orElse: () => item);
    if (liveItem.todoColumnCount >= 4) return;

    final nextColNumber = liveItem.todoColumnCount + 1;
    _editColumnDialog(livePaper, liveItem, nextColNumber, isNewColumn: true);
  }

  void _decreaseColumns(PaperData paper, PaperItem item) {
    final livePaper = _state.papers.firstWhere((p) => p.id == paper.id, orElse: () => paper);
    final liveItem = livePaper.items.firstWhere((i) => i.id == item.id, orElse: () => item);
    if (liveItem.todoColumnCount <= 1) return;

    _deleteColumn(livePaper, liveItem, liveItem.todoColumnCount);
  }

  void _deleteColumn(PaperData paper, PaperItem item, int colNumber) {
    final livePaper = _state.papers.firstWhere((p) => p.id == paper.id, orElse: () => paper);
    final liveItem = livePaper.items.firstWhere((i) => i.id == item.id, orElse: () => item);
    if (liveItem.todoColumnCount <= 1) return;

    setState(() {
      final deleteIndex = colNumber - 1; // 0-based
      if (deleteIndex == 0) {
        liveItem.text = liveItem.todoExtraColumns.isNotEmpty ? liveItem.todoExtraColumns.removeAt(0) : '';
      } else {
        final extraIdx = deleteIndex - 1;
        if (extraIdx < liveItem.todoExtraColumns.length) {
          liveItem.todoExtraColumns.removeAt(extraIdx);
        }
      }
      liveItem.todoColumnCount = (liveItem.todoExtraColumns.length + 1).clamp(1, 4);
      liveItem.updatedAt = DateTime.now().millisecondsSinceEpoch;
      livePaper.updatedAt = liveItem.updatedAt;
    });
    _saveAndNotify();
  }

  void _commitColumnEdit(PaperData paper, PaperItem item, int colNumber, String newText, bool isNewColumn) {
    final livePaper = _state.papers.firstWhere((p) => p.id == paper.id, orElse: () => paper);
    final liveItem = livePaper.items.firstWhere((i) => i.id == item.id, orElse: () => item);

    setState(() {
      if (colNumber == 1) {
        liveItem.text = newText;
      } else {
        final extraIdx = colNumber - 2;
        while (liveItem.todoExtraColumns.length <= extraIdx) {
          liveItem.todoExtraColumns.add('');
        }
        liveItem.todoExtraColumns[extraIdx] = newText;
      }
      liveItem.todoColumnCount = (liveItem.todoExtraColumns.length + 1).clamp(1, 4);
      liveItem.updatedAt = DateTime.now().millisecondsSinceEpoch;
      livePaper.updatedAt = liveItem.updatedAt;
    });
    _saveAndNotify();
  }

  void _editColumnDialog(PaperData paper, PaperItem item, int colNumber, {bool isNewColumn = false}) {
    final livePaper = _state.papers.firstWhere((p) => p.id == paper.id, orElse: () => paper);
    final liveItem = livePaper.items.firstWhere((i) => i.id == item.id, orElse: () => item);

    final initialText = isNewColumn
        ? ''
        : (colNumber == 1
            ? liveItem.text
            : (colNumber - 2 < liveItem.todoExtraColumns.length ? liveItem.todoExtraColumns[colNumber - 2] : ''));

    final textController = TextEditingController(text: initialText);

    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: Colors.white,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
        title: Text(
          isNewColumn ? '添加第 $colNumber 列' : '编辑第 $colNumber 列内容',
          style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 17, color: MiuixColors.textPrimary),
        ),
        content: TextField(
          controller: textController,
          autofocus: true,
          decoration: InputDecoration(
            labelText: colNumber == 1 ? '待办主内容' : '第 $colNumber 列扩展内容',
            hintText: '例如负责人、备注、标签等',
            border: const OutlineInputBorder(),
          ),
          onSubmitted: (_) {
            _commitColumnEdit(livePaper, liveItem, colNumber, textController.text.trim(), isNewColumn);
            Navigator.pop(ctx);
          },
        ),
        actions: [
          if (!isNewColumn && (colNumber > 1 || liveItem.todoColumnCount > 1))
            OutlinedButton.icon(
              icon: const Icon(Icons.delete_outline, size: 16, color: MiuixColors.error),
              label: const Text('删除此列', style: TextStyle(color: MiuixColors.error, fontWeight: FontWeight.w600)),
              style: OutlinedButton.styleFrom(
                side: const BorderSide(color: Color(0xFFFFCDD2)),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
              ),
              onPressed: () {
                _deleteColumn(livePaper, liveItem, colNumber);
                Navigator.pop(ctx);
              },
            ),
          TextButton(
            style: TextButton.styleFrom(foregroundColor: MiuixColors.textSecondary),
            onPressed: () => Navigator.pop(ctx),
            child: const Text('取消'),
          ),
          ElevatedButton(
            style: ElevatedButton.styleFrom(
              backgroundColor: MiuixColors.primary,
              foregroundColor: Colors.white,
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
            ),
            onPressed: () {
              _commitColumnEdit(livePaper, liveItem, colNumber, textController.text.trim(), isNewColumn);
              Navigator.pop(ctx);
            },
            child: const Text('保存'),
          ),
        ],
      ),
    );
  }

  void _editAllColumnsDialog(PaperData paper, PaperItem item) {
    final controllers = <TextEditingController>[
      TextEditingController(text: item.text),
      ...item.todoExtraColumns.map((c) => TextEditingController(text: c)),
    ];
    while (controllers.length < item.todoColumnCount) {
      controllers.add(TextEditingController(text: ''));
    }

    showDialog(
      context: context,
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setDialogState) => AlertDialog(
          backgroundColor: Colors.white,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
          title: Row(
            children: [
              const Icon(Icons.view_column_rounded, color: MiuixColors.primary, size: 22),
              const SizedBox(width: 8),
              Text('编辑多列 (${item.todoColumnCount} 列)', style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 17, color: MiuixColors.textPrimary)),
            ],
          ),
          content: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (var i = 0; i < item.todoColumnCount; i++) ...[
                  Row(
                    children: [
                      Text(
                        i == 0 ? '第 1 列 (主待办)' : '第 ${i + 1} 列 (扩展信息)',
                        style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 13, color: MiuixColors.textPrimary),
                      ),
                      const Spacer(),
                      if (item.todoColumnCount > 1 && i > 0)
                        InkWell(
                          borderRadius: BorderRadius.circular(6),
                          onTap: () {
                            setDialogState(() {
                              controllers.removeAt(i);
                              item.todoColumnCount -= 1;
                            });
                          },
                          child: const Padding(
                            padding: EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Icon(Icons.delete_outline, size: 14, color: MiuixColors.error),
                                SizedBox(width: 2),
                                Text('删除此列', style: TextStyle(color: MiuixColors.error, fontSize: 11.5, fontWeight: FontWeight.w600)),
                              ],
                            ),
                          ),
                        ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  TextField(
                    controller: controllers[i],
                    decoration: InputDecoration(
                      hintText: i == 0 ? '待办任务内容' : '例如负责人、备注等',
                      border: const OutlineInputBorder(),
                      isDense: true,
                    ),
                  ),
                  const SizedBox(height: 12),
                ],
                const Divider(color: MiuixColors.divider),
                Row(
                  children: [
                    if (item.todoColumnCount < 4)
                      OutlinedButton.icon(
                        icon: const Icon(Icons.add, size: 15, color: MiuixColors.primary),
                        label: const Text('增加一列', style: TextStyle(color: MiuixColors.primary)),
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(color: Color(0xFFBBDEFB)),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
                        ),
                        onPressed: () {
                          setDialogState(() {
                            item.todoColumnCount += 1;
                            controllers.add(TextEditingController(text: ''));
                          });
                        },
                      ),
                    const SizedBox(width: 8),
                    if (item.todoColumnCount > 1)
                      OutlinedButton.icon(
                        icon: const Icon(Icons.remove, size: 15, color: MiuixColors.error),
                        label: const Text('减少一列', style: TextStyle(color: MiuixColors.error)),
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(color: Color(0xFFFFCDD2)),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
                        ),
                        onPressed: () {
                          setDialogState(() {
                            item.todoColumnCount -= 1;
                            if (controllers.length > item.todoColumnCount) {
                              controllers.removeLast();
                            }
                          });
                        },
                      ),
                  ],
                ),
              ],
            ),
          ),
          actions: [
            TextButton(
              style: TextButton.styleFrom(foregroundColor: MiuixColors.textSecondary),
              onPressed: () => Navigator.pop(ctx),
              child: const Text('取消'),
            ),
            ElevatedButton(
              style: ElevatedButton.styleFrom(
                backgroundColor: MiuixColors.primary,
                foregroundColor: Colors.white,
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
              ),
              onPressed: () {
                setState(() {
                  item.text = controllers[0].text;
                  item.todoExtraColumns = controllers
                      .skip(1)
                      .take(item.todoColumnCount - 1)
                      .map((c) => c.text)
                      .toList();
                  while (item.todoExtraColumns.length < item.todoColumnCount - 1) {
                    item.todoExtraColumns.add('');
                  }
                  item.updatedAt = DateTime.now().millisecondsSinceEpoch;
                  paper.updatedAt = item.updatedAt;
                });
                Navigator.pop(ctx);
                _saveAndNotify();
              },
              child: const Text('保存修改'),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _pickTodoDueDate(PaperData paper, PaperItem item) async {
    final choice = await showDueDateSheet(
      context: context,
      taskText: item.text,
      initialValue: _parseDue(item.dueAtLocal),
      canClear: item.dueAtLocal?.isNotEmpty == true,
    );
    if (!mounted || choice == null) return;

    // A remote deletion while the sheet is open must not resurrect an old item.
    final livePaper = _state.papers.where((p) => p.id == paper.id).firstOrNull;
    final liveItem = livePaper?.items.where((i) => i.id == item.id).firstOrNull;
    if (livePaper == null || liveItem == null) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('这条待办已被删除，时间未保存。')));
      return;
    }
    setState(() {
      liveItem.dueAtLocal = choice.value == null ? null : DateFormat('yyyy-MM-ddTHH:mm:ss').format(choice.value!);
      liveItem.updatedAt = DateTime.now().millisecondsSinceEpoch;
      livePaper.updatedAt = liveItem.updatedAt;
    });
    await _saveAndNotify();
  }

  void _showNotePreviewSheet(PaperData note) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      builder: (ctx) => Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.description, size: 20, color: Color(0xFFB07D48)),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    note.title.isEmpty ? '便签笔记' : note.title,
                    style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 17),
                  ),
                ),
                IconButton(icon: const Icon(Icons.close), onPressed: () => Navigator.pop(ctx)),
              ],
            ),
            const Divider(),
            const SizedBox(height: 8),
            SelectableText(
              note.content.isEmpty ? '（空笔记内容）' : note.content,
              style: const TextStyle(fontSize: 15, height: 1.6),
            ),
            const SizedBox(height: 24),
          ],
        ),
      ),
    );
  }

  Widget _buildFastAppendRow(PaperData paper) {
    final controller = TextEditingController();
    return Container(
      margin: const EdgeInsets.only(top: 6),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 2),
      decoration: BoxDecoration(
        color: MiuixColors.tagBg,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        children: [
          Expanded(
            child: TextField(
              controller: controller,
              decoration: const InputDecoration(
                hintText: '添加新待办...',
                hintStyle: TextStyle(fontSize: 13.5, color: MiuixColors.textSecondary),
                border: InputBorder.none,
                isDense: true,
              ),
              style: const TextStyle(fontSize: 14, color: MiuixColors.textPrimary),
              onSubmitted: (text) {
                if (text.trim().isNotEmpty) {
                  setState(() {
                    paper.items.add(PaperItem(
                      text: text.trim(),
                      order: paper.items.length,
                    ));
                    paper.updatedAt = DateTime.now().millisecondsSinceEpoch;
                  });
                  controller.clear();
                  _saveAndNotify();
                }
              },
            ),
          ),
          IconButton(
            icon: const Icon(Icons.add_circle, color: MiuixColors.primary, size: 24),
            onPressed: () {
              final text = controller.text.trim();
              if (text.isNotEmpty) {
                setState(() {
                  paper.items.add(PaperItem(
                    text: text,
                    order: paper.items.length,
                  ));
                  paper.updatedAt = DateTime.now().millisecondsSinceEpoch;
                });
                controller.clear();
                _saveAndNotify();
              }
            },
          ),
        ],
      ),
    );
  }

  void _deletePaper(PaperData paper) {
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('确认删除纸片'),
        content: Text('确定要删除“${paper.title.isEmpty ? '待办清单' : paper.title}”吗？'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('取消')),
          TextButton(
            onPressed: () {
              Navigator.pop(ctx);
              setState(() {
                _state.papers.removeWhere((p) => p.id == paper.id);
                _state.recordDeletedPaper(paper.id);
              });
              _saveAndNotify();
            },
            child: const Text('删除', style: TextStyle(color: Colors.red)),
          ),
        ],
      ),
    );
  }

  void _editNoteContent(PaperData paper) {
    final textController = TextEditingController(text: paper.content);
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      builder: (ctx) => Padding(
        padding: EdgeInsets.only(
          bottom: MediaQuery.of(ctx).viewInsets.bottom,
          left: 16,
          right: 16,
          top: 16,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(
                  paper.title.isEmpty ? '编辑便签' : paper.title,
                  style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 16),
                ),
                IconButton(icon: const Icon(Icons.close), onPressed: () => Navigator.pop(ctx)),
              ],
            ),
            const SizedBox(height: 8),
            TextField(
              controller: textController,
              maxLines: 10,
              minLines: 4,
              decoration: const InputDecoration(
                border: OutlineInputBorder(),
                hintText: '输入便签或 Markdown 内容...',
              ),
            ),
            const SizedBox(height: 12),
            ElevatedButton(
              style: ElevatedButton.styleFrom(
                backgroundColor: const Color(0xFFD4A373),
                foregroundColor: Colors.white,
              ),
              onPressed: () {
                setState(() {
                  paper.content = textController.text;
                  paper.updatedAt = DateTime.now().millisecondsSinceEpoch;
                });
                Navigator.pop(ctx);
                _saveAndNotify();
              },
              child: const Text('保存'),
            ),
            const SizedBox(height: 16),
          ],
        ),
      ),
    );
  }

  void _showCreatePaperDialog() {
    final titleController = TextEditingController();
    String selectedType = 'todo';

    showDialog(
      context: context,
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setDialogState) => AlertDialog(
          title: const Text('新建纸片'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: titleController,
                decoration: const InputDecoration(
                  labelText: '纸片标题',
                  hintText: '例如：工作待办、灵感',
                ),
              ),
              const SizedBox(height: 16),
              SegmentedButton<String>(
                segments: const [
                  ButtonSegment(value: 'todo', label: Text('待办清单'), icon: Icon(Icons.check_box_outlined)),
                  ButtonSegment(value: 'note', label: Text('便签笔记'), icon: Icon(Icons.edit_note_rounded)),
                ],
                selected: {selectedType},
                onSelectionChanged: (val) {
                  setDialogState(() => selectedType = val.first);
                },
              ),
            ],
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('取消')),
            ElevatedButton(
              style: ElevatedButton.styleFrom(
                backgroundColor: const Color(0xFFD4A373),
                foregroundColor: Colors.white,
              ),
              onPressed: () {
                final title = titleController.text.trim();
                setState(() {
                  _state.papers.add(PaperData(
                    title: title,
                    type: selectedType,
                  ));
                });
                Navigator.pop(ctx);
                _saveAndNotify();
              },
              child: const Text('创建'),
            ),
          ],
        ),
      ),
    );
  }
}

class SettingsScreen extends StatefulWidget {
  final AppState state;
  const SettingsScreen({super.key, required this.state});

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  late bool _syncEnabled;
  late String _syncProvider;
  late String _mobileViewMode;
  late bool _notificationsEnabled;
  late int _notificationLeadMinutes;
  late bool _notifyOverdue;
  late TextEditingController _webdavUrlCtrl;
  late TextEditingController _webdavUserCtrl;
  late TextEditingController _webdavPassCtrl;
  late TextEditingController _webdavPathCtrl;
  late TextEditingController _s3EndpointCtrl;
  late TextEditingController _s3BucketCtrl;
  late TextEditingController _s3KeyCtrl;
  late TextEditingController _s3SecretCtrl;
  late TextEditingController _s3RegionCtrl;
  late TextEditingController _customUrlCtrl;
  late TextEditingController _customTokenCtrl;

  bool _isTesting = false;
  String? _testMessage;
  bool _testSuccess = false;

  @override
  void initState() {
    super.initState();
    final s = widget.state;
    _syncEnabled = s.syncEnabled;
    _syncProvider = s.syncProvider;
    _mobileViewMode = s.mobileViewMode;
    _notificationsEnabled = s.notificationsEnabled;
    _notificationLeadMinutes = s.notificationLeadMinutes;
    _notifyOverdue = s.notifyOverdue;
    _webdavUrlCtrl = TextEditingController(text: s.syncWebDavUrl);
    _webdavUserCtrl = TextEditingController(text: s.syncWebDavUsername);
    _webdavPassCtrl = TextEditingController(text: s.syncWebDavPassword);
    _webdavPathCtrl = TextEditingController(text: s.syncWebDavRemotePath);
    _s3EndpointCtrl = TextEditingController(text: s.syncS3Endpoint);
    _s3BucketCtrl = TextEditingController(text: s.syncS3Bucket);
    _s3KeyCtrl = TextEditingController(text: s.syncS3AccessKey);
    _s3SecretCtrl = TextEditingController(text: s.syncS3SecretKey);
    _s3RegionCtrl = TextEditingController(text: s.syncS3Region);
    _customUrlCtrl = TextEditingController(text: s.syncCustomServerUrl);
    _customTokenCtrl = TextEditingController(text: s.syncCustomToken);
  }

  Future<void> _saveSettings() async {
    final s = widget.state;
    s.syncEnabled = _syncEnabled;
    s.syncProvider = _syncProvider;
    s.mobileViewMode = _mobileViewMode;
    s.notificationsEnabled = _notificationsEnabled;
    s.notificationLeadMinutes = _notificationLeadMinutes;
    s.notifyOverdue = _notifyOverdue;
    s.syncWebDavUrl = _webdavUrlCtrl.text.trim();
    s.syncWebDavUsername = _webdavUserCtrl.text.trim();
    s.syncWebDavPassword = _webdavPassCtrl.text;
    s.syncWebDavRemotePath = _webdavPathCtrl.text.trim();
    s.syncS3Endpoint = _s3EndpointCtrl.text.trim();
    s.syncS3Bucket = _s3BucketCtrl.text.trim();
    s.syncS3AccessKey = _s3KeyCtrl.text.trim();
    s.syncS3SecretKey = _s3SecretCtrl.text;
    s.syncS3Region = _s3RegionCtrl.text.trim();
    s.syncCustomServerUrl = _customUrlCtrl.text.trim();
    s.syncCustomToken = _customTokenCtrl.text.trim();
    try {
      await SyncManager.saveLocalState(s);
      await NotificationService.checkDueReminders(s);
    } catch (error) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('设置保存失败：$error')));
    }
  }

  Future<void> _testConnection() async {
    await _saveSettings();
    if (!mounted) return;
    setState(() {
      _isTesting = true;
      _testMessage = null;
    });

    final provider = SyncManager.createProvider(widget.state);
    final res = await provider.testConnection();
    if (!mounted) return;
    setState(() {
      _isTesting = false;
      _testMessage = res.message;
      _testSuccess = res.success;
    });
  }

  @override
  Widget build(BuildContext context) {
    final lastTimeStr = widget.state.lastSyncTimeUtc > 0
        ? DateFormat('yyyy-MM-dd HH:mm:ss').format(DateTime.fromMillisecondsSinceEpoch(widget.state.lastSyncTimeUtc))
        : '从未';

    return Scaffold(
      backgroundColor: MiuixColors.background,
      appBar: AppBar(
        title: const Text('应用设置', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 19, color: MiuixColors.textPrimary)),
        backgroundColor: MiuixColors.background,
        elevation: 0,
        scrolledUnderElevation: 0,
        iconTheme: const IconThemeData(color: MiuixColors.textPrimary),
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          // View Mode Selector Card
          Card(child: Padding(padding: const EdgeInsets.all(15), child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text('纸片备份与迁移', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 15.5)),
              const SizedBox(height: 8),
              const Text('导出包含纸片与删除记录，不包含同步密码。更换签名或卸载前，请先导出并确认备份文件可读取。导入会与现有纸片合并。'),
              const SizedBox(height: 10),
              Wrap(spacing: 10, children: [
                OutlinedButton.icon(icon: const Icon(Icons.file_upload_outlined), label: const Text('导出纸片'), onPressed: () async {
                  try {
                    final written = await BackupTransfer.exportPapers(widget.state);
                    if (context.mounted && written) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('纸片备份已导出。')));
                  } catch (error) {
                    if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('导出失败：$error')));
                  }
                }),
                OutlinedButton.icon(icon: const Icon(Icons.file_download_outlined), label: const Text('导入纸片'), onPressed: () async {
                  try {
                    final imported = await BackupTransfer.importPapers(widget.state);
                    if (context.mounted && imported) {
                      setState(() {});
                      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('纸片已合并导入。')));
                      await NotificationService.checkDueReminders(widget.state);
                    }
                  } catch (error) {
                    if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('导入失败，原数据保留：$error')));
                  }
                }),
              ]),
            ],
          ))),
          Card(
            margin: const EdgeInsets.only(bottom: 16),
            child: Padding(
              padding: const EdgeInsets.all(15),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text('卡片浏览模式', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 15.5, color: MiuixColors.textPrimary)),
                  const SizedBox(height: 6),
                  const Text(
                    '书签导航：左上角书签按钮可随时展开目录快速跳转，单张便签沉浸翻页\n卡片列表流：垂直滑动浏览全部便签卡片',
                    style: TextStyle(fontSize: 12.5, color: MiuixColors.textSecondary, height: 1.4),
                  ),
                  const SizedBox(height: 12),
                  SegmentedButton<String>(
                    segments: const [
                      ButtonSegment(
                        value: 'bookmark',
                        label: Text('📑 书签导航'),
                      ),
                      ButtonSegment(
                        value: 'flow',
                        label: Text('📜 卡片列表流'),
                      ),
                    ],
                    selected: {_mobileViewMode},
                    onSelectionChanged: (val) {
                      setState(() => _mobileViewMode = val.first);
                      _saveSettings();
                    },
                  ),
                ],
              ),
            ),
          ),
          Card(
            margin: const EdgeInsets.only(bottom: 16),
            child: Padding(
              padding: const EdgeInsets.all(15),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      const Icon(Icons.notifications_active_outlined, color: MiuixColors.primary, size: 20),
                      const SizedBox(width: 8),
                      const Text('待办提醒与通知', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 15.5, color: MiuixColors.textPrimary)),
                      const Spacer(),
                      Switch(
                        activeThumbColor: MiuixColors.primary,
                        value: _notificationsEnabled,
                        onChanged: (val) async {
                          if (val) {
                            await NotificationService.requestPermission();
                          }
                          setState(() => _notificationsEnabled = val);
                          _saveSettings();
                        },
                      ),
                    ],
                  ),
                  const Text(
                    '在时间节点临近及事项逾期时，在系统通知栏发送提醒',
                    style: TextStyle(fontSize: 12.5, color: MiuixColors.textSecondary),
                  ),
                  if (_notificationsEnabled) ...[
                    const Divider(height: 20, color: MiuixColors.divider),
                    const Text('到期提前提醒时间', style: TextStyle(fontWeight: FontWeight.w600, fontSize: 13.5, color: MiuixColors.textPrimary)),
                    const SizedBox(height: 8),
                    SegmentedButton<int>(
                      segments: const [
                        ButtonSegment(value: 15, label: Text('提前 15 分钟')),
                        ButtonSegment(value: 30, label: Text('提前 30 分钟')),
                        ButtonSegment(value: 60, label: Text('提前 1 小时')),
                      ],
                      selected: {_notificationLeadMinutes},
                      onSelectionChanged: (val) {
                        setState(() => _notificationLeadMinutes = val.first);
                        _saveSettings();
                      },
                    ),
                    const SizedBox(height: 10),
                    CheckboxListTile(
                      contentPadding: EdgeInsets.zero,
                      title: const Text('逾期事项提醒', style: TextStyle(fontSize: 13.5, fontWeight: FontWeight.w600, color: MiuixColors.textPrimary)),
                      subtitle: const Text('已超过截止时间但未完成的待办事项提醒', style: TextStyle(fontSize: 12, color: MiuixColors.textSecondary)),
                      activeColor: MiuixColors.primary,
                      value: _notifyOverdue,
                      onChanged: (val) {
                        setState(() => _notifyOverdue = val ?? true);
                        _saveSettings();
                      },
                    ),
                    const SizedBox(height: 8),
                    SizedBox(
                      width: double.infinity,
                      child: OutlinedButton.icon(
                        icon: const Icon(Icons.notifications_outlined, size: 16),
                        label: const Text('发送测试通知'),
                        style: OutlinedButton.styleFrom(
                          foregroundColor: MiuixColors.primary,
                          side: const BorderSide(color: Color(0xFFBBDEFB)),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
                        ),
                        onPressed: () async {
                          final sent = await NotificationService.sendTestNotification();
                          if (context.mounted) {
                            ScaffoldMessenger.of(context).showSnackBar(
                              SnackBar(content: Text(sent ? '已发送测试通知，请查看手机通知栏' : '通知未发送，请在系统设置中允许 PaperTodo 通知。'), duration: const Duration(seconds: 2)),
                            );
                          }
                        },
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
          Card(
            margin: const EdgeInsets.only(bottom: 16),
            child: Padding(
              padding: const EdgeInsets.all(15),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      const Icon(Icons.cloud_sync_outlined, color: MiuixColors.primary, size: 20),
                      const SizedBox(width: 8),
                      const Text('数据云同步', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 15.5, color: MiuixColors.textPrimary)),
                      const Spacer(),
                      Switch(
                        activeThumbColor: MiuixColors.primary,
                        value: _syncEnabled,
                        onChanged: (val) {
                          setState(() => _syncEnabled = val);
                          _saveSettings();
                        },
                      ),
                    ],
                  ),
                  const Text('与电脑端保持数据双向即时同步', style: TextStyle(fontSize: 12.5, color: MiuixColors.textSecondary)),
                  if (_syncEnabled) ...[
                    const Divider(height: 20, color: MiuixColors.divider),
                    const Text('同步协议', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 13.5, color: MiuixColors.textPrimary)),
                    const SizedBox(height: 8),
            const SizedBox(height: 8),
            SegmentedButton<String>(
              segments: const [
                ButtonSegment(value: 'webdav', label: Text('WebDAV')),
                ButtonSegment(value: 's3', label: Text('S3 存储')),
                ButtonSegment(value: 'custom', label: Text('自建服务')),
              ],
              selected: {_syncProvider},
              onSelectionChanged: (val) {
                setState(() => _syncProvider = val.first);
                _saveSettings();
              },
            ),
            const SizedBox(height: 16),
            if (_syncProvider == 'webdav') ...[
              TextField(
                controller: _webdavUrlCtrl,
                decoration: const InputDecoration(
                  labelText: 'WebDAV 服务器地址 (URL)',
                  hintText: 'https://dav.jianguoyun.com/dav/',
                  border: OutlineInputBorder(),
                ),
                onChanged: (_) => _saveSettings(),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _webdavUserCtrl,
                decoration: const InputDecoration(
                  labelText: '账号 (坚果云邮箱)',
                  border: OutlineInputBorder(),
                ),
                onChanged: (_) => _saveSettings(),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _webdavPassCtrl,
                obscureText: true,
                decoration: const InputDecoration(
                  labelText: '应用密码',
                  border: OutlineInputBorder(),
                ),
                onChanged: (_) => _saveSettings(),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _webdavPathCtrl,
                decoration: const InputDecoration(
                  labelText: '远端文件路径',
                  hintText: '/papertodo/data.json',
                  border: OutlineInputBorder(),
                ),
                onChanged: (_) => _saveSettings(),
              ),
            ] else if (_syncProvider == 's3') ...[
              TextField(
                controller: _s3EndpointCtrl,
                decoration: const InputDecoration(
                  labelText: 'S3 Endpoint (端点)',
                  hintText: 'https://<account_id>.r2.cloudflarestorage.com',
                  border: OutlineInputBorder(),
                ),
                onChanged: (_) => _saveSettings(),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _s3BucketCtrl,
                decoration: const InputDecoration(labelText: 'Bucket 名称', border: OutlineInputBorder()),
                onChanged: (_) => _saveSettings(),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _s3KeyCtrl,
                decoration: const InputDecoration(labelText: 'Access Key', border: OutlineInputBorder()),
                onChanged: (_) => _saveSettings(),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _s3SecretCtrl,
                obscureText: true,
                decoration: const InputDecoration(labelText: 'Secret Key', border: OutlineInputBorder()),
                onChanged: (_) => _saveSettings(),
              ),
            ] else ...[
              TextField(
                controller: _customUrlCtrl,
                decoration: const InputDecoration(
                  labelText: '服务地址 (URL)',
                  hintText: 'http://your-server-ip:8080',
                  border: OutlineInputBorder(),
                ),
                onChanged: (_) => _saveSettings(),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _customTokenCtrl,
                obscureText: true,
                decoration: const InputDecoration(
                  labelText: '访问密钥 (Token)',
                  border: OutlineInputBorder(),
                ),
                onChanged: (_) => _saveSettings(),
              ),
            ],
            const SizedBox(height: 18),
            Row(
              children: [
                Expanded(
                  child: ElevatedButton(
                    style: ElevatedButton.styleFrom(
                      backgroundColor: MiuixColors.primary,
                      foregroundColor: Colors.white,
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
                      padding: const EdgeInsets.symmetric(vertical: 12),
                    ),
                    onPressed: _isTesting ? null : _testConnection,
                    child: _isTesting
                        ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                        : const Text('测试连接', style: TextStyle(fontWeight: FontWeight.bold)),
                  ),
                ),
              ],
            ),
            if (_testMessage != null) ...[
              const SizedBox(height: 8),
              Text(
                _testMessage!,
                style: TextStyle(
                  fontSize: 13,
                  color: _testSuccess ? Colors.green.shade700 : MiuixColors.error,
                  fontWeight: FontWeight.bold,
                ),
              ),
            ],
            const SizedBox(height: 14),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: MiuixColors.tagBg,
                borderRadius: BorderRadius.circular(10),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('上次同步时间: $lastTimeStr', style: const TextStyle(fontSize: 12, color: MiuixColors.textSecondary)),
                  const SizedBox(height: 4),
                  Text('状态: ${widget.state.lastSyncStatus.isEmpty ? '-' : widget.state.lastSyncStatus}',
                      style: const TextStyle(fontSize: 12, color: MiuixColors.textSecondary)),
                ],
              ),
            ),
          ],
        ],
      ),
    ),
  ),
],
),
);
}
}

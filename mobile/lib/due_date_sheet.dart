import 'dart:math' as math;
import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'due_date_calendar.dart';

class DueDateChoice {
  final DateTime? value;
  const DueDateChoice(this.value);
}

bool isSameCalendarDay(DateTime a, DateTime b) =>
    a.year == b.year && a.month == b.month && a.day == b.day;

String shortDueDate(DateTime date, DateTime now) {
  if (isSameCalendarDay(date, now)) return '今天';
  if (isSameCalendarDay(date, DateTime(now.year, now.month, now.day + 1))) return '明天';
  return DateFormat(date.year == now.year ? 'M月d日' : 'yyyy年M月d日').format(date);
}

Future<DueDateChoice?> showDueDateSheet({
  required BuildContext context,
  required String taskText,
  DateTime? initialValue,
  bool? canClear,
  DateTime Function()? clock,
}) {
  FocusScope.of(context).unfocus();
  return showModalBottomSheet<DueDateChoice>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    backgroundColor: Colors.transparent,
    constraints: const BoxConstraints(maxWidth: 560),
    builder: (_) => DueDateSheet(taskText: taskText, initialValue: initialValue, canClear: canClear, clock: clock),
  );
}

class DueDateSheet extends StatefulWidget {
  final String taskText;
  final DateTime? initialValue;
  final bool? canClear;
  final DateTime Function()? clock;

  const DueDateSheet({super.key, required this.taskText, this.initialValue, this.canClear, this.clock});

  @override
  State<DueDateSheet> createState() => _DueDateSheetState();
}

class _DueDateSheetState extends State<DueDateSheet> {
  static const _accent = Color(0xFF2563DB);
  static const _ink = Color(0xFF20242C);
  static const _secondary = Color(0xFF626873);
  static const _surface = Color(0xFFF5F6F8);
  static const _selection = Color(0xFFEAF1FF);
  static const _weekdays = ['周一', '周二', '周三', '周四', '周五', '周六', '周日'];

  late DateTime _date;
  late int _hour;
  late int _minute;
  late final FixedExtentScrollController _hours;
  late final FixedExtentScrollController _minutes;
  String? _dateShortcut;
  bool _calendarExpanded = false;
  bool _revealCalendarPending = false;
  final _calendarAnchor = GlobalKey();

  DateTime get _now => widget.clock?.call() ?? DateTime.now();
  DateTime get _value => DateTime(_date.year, _date.month, _date.day, _hour, _minute);
  String get _time => '${_hour.toString().padLeft(2, '0')}:${_minute.toString().padLeft(2, '0')}';
  bool get _canClear => widget.canClear ?? widget.initialValue != null;

  List<({String label, DateTime date})> get _dates {
    final now = _now;
    final monday = (DateTime.monday - now.weekday + 7) % 7;
    return [
      (label: '今天', date: DateTime(now.year, now.month, now.day)),
      (label: '明天', date: DateTime(now.year, now.month, now.day + 1)),
      (label: '后天', date: DateTime(now.year, now.month, now.day + 2)),
      (label: '下周一', date: DateTime(now.year, now.month, now.day + (monday == 0 ? 7 : monday))),
    ];
  }

  @override
  void initState() {
    super.initState();
    final initial = widget.initialValue ?? _now.add(const Duration(hours: 1));
    _date = DateTime(initial.year, initial.month, initial.day);
    _hour = initial.hour;
    _minute = initial.minute;
    _hours = FixedExtentScrollController(initialItem: _hour);
    _minutes = FixedExtentScrollController(initialItem: _minute);
    _updateDateShortcut();
  }

  @override
  void dispose() {
    _hours.dispose();
    _minutes.dispose();
    super.dispose();
  }

  void _updateDateShortcut() {
    _dateShortcut = null;
    for (final option in _dates) {
      if (isSameCalendarDay(option.date, _date)) { _dateShortcut = option.label; break; }
    }
  }

  void _chooseDate() {
    setState(() {
      _calendarExpanded = !_calendarExpanded;
      _revealCalendarPending = _calendarExpanded;
    });
    if (MediaQuery.disableAnimationsOf(context)) {
      _revealCalendar();
    }
  }

  void _revealCalendar() {
    if (!mounted || !_calendarExpanded || !_revealCalendarPending) return;
    _revealCalendarPending = false;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_calendarExpanded) return;
      final target = _calendarAnchor.currentContext;
      if (target == null) return;
      Scrollable.ensureVisible(target, alignment: 0,
          duration: MediaQuery.disableAnimationsOf(context) ? Duration.zero : const Duration(milliseconds: 160),
          curve: Curves.easeOut);
    });
  }

  void _chooseTime(int hour, int minute) {
    _hours.jumpToItem(hour);
    _minutes.jumpToItem(minute);
    setState(() { _hour = hour; _minute = minute; });
  }

  Widget _shortcut(String label, bool selected, VoidCallback onTap, Key key, {bool compact = false}) => ChoiceChip(
    key: key,
    label: Text(label),
    selected: selected,
    showCheckmark: false,
    onSelected: (_) => onTap(),
    selectedColor: _selection,
    backgroundColor: Colors.white,
    side: BorderSide(color: selected ? _accent : const Color(0xFFE2E5EA)),
    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
    labelStyle: TextStyle(color: selected ? _accent : _secondary, fontSize: 14,
        fontWeight: selected ? FontWeight.w600 : FontWeight.w500),
    labelPadding: EdgeInsets.symmetric(horizontal: compact ? 0 : 4),
    padding: EdgeInsets.symmetric(horizontal: compact ? 4 : 6, vertical: 7),
    materialTapTargetSize: MaterialTapTargetSize.padded,
  );

  Widget _wheel({required bool hour, required double extent}) {
    final selected = hour ? _hour : _minute;
    return Semantics(
      label: hour ? '小时' : '分钟',
      child: CupertinoPicker(
        key: ValueKey(hour ? 'due-hour-wheel' : 'due-minute-wheel'),
        scrollController: hour ? _hours : _minutes,
        itemExtent: extent,
        diameterRatio: 1.25,
        selectionOverlay: null,
        looping: true,
        onSelectedItemChanged: (value) => setState(() { if (hour) { _hour = value; } else { _minute = value; } }),
        children: List.generate(hour ? 24 : 60, (value) => Center(
          child: Semantics(
            label: '$value${hour ? '时' : '分'}',
            excludeSemantics: true,
            child: Text(value.toString().padLeft(2, '0'), style: TextStyle(
              fontSize: 32, height: 1.15,
              fontFeatures: const [FontFeature.tabularFigures()],
              color: selected == value ? _accent : _secondary,
              fontWeight: selected == value ? FontWeight.w600 : FontWeight.w400,
            )),
          ),
        )),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final now = _now;
    final past = _value.isBefore(now);
    final scale = MediaQuery.textScalerOf(context).scale(16) / 16;
    final extent = 48.0 * math.max(1.0, scale);
    final keyboard = MediaQuery.viewInsetsOf(context).bottom;
    final dateText = DateFormat(_date.year == now.year ? 'M月d日' : 'yyyy年M月d日').format(_date);
    final weekday = _weekdays[_date.weekday - 1];
    final dateLabel = shortDueDate(_date, now);
    final summary = '$dateLabel $_time';

    return Padding(
      padding: EdgeInsets.only(bottom: keyboard),
      child: ConstrainedBox(
        constraints: BoxConstraints(maxHeight: math.max(0, (MediaQuery.sizeOf(context).height - keyboard) * .94)),
        child: Material(
          color: Colors.white,
          clipBehavior: Clip.antiAlias,
          borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
          child: SafeArea(top: false, child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Center(child: Container(width: 36, height: 4, margin: const EdgeInsets.only(top: 10, bottom: 4),
                  decoration: BoxDecoration(color: const Color(0xFFD8DCE3), borderRadius: BorderRadius.circular(2)))),
              Padding(padding: const EdgeInsets.fromLTRB(20, 0, 8, 0), child: Row(children: [
                const Expanded(child: Text('截止时间', style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700, color: _ink))),
                IconButton(key: const ValueKey('due-close'), tooltip: '关闭', onPressed: () => Navigator.pop(context),
                    icon: const Icon(Icons.close_rounded, color: _secondary)),
              ])),
              Flexible(fit: FlexFit.loose, child: SingleChildScrollView(
                key: const ValueKey('due-scroll'),
                padding: const EdgeInsets.fromLTRB(20, 0, 20, 16),
                child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                  if (widget.taskText.trim().isNotEmpty) ...[
                    Text(widget.taskText, maxLines: 2, overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontSize: 14, height: 1.45, color: _secondary)),
                    const SizedBox(height: 20),
                  ],
                  const Text('日期', style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600, color: _ink)),
                  const SizedBox(height: 8),
                  Material(color: _surface, borderRadius: BorderRadius.circular(14), child: InkWell(
                    key: const ValueKey('due-calendar'),
                    borderRadius: BorderRadius.circular(14), onTap: _chooseDate,
                    child: Padding(padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 13), child: Row(children: [
                      const Icon(Icons.calendar_month_outlined, color: _accent, size: 22),
                      const SizedBox(width: 12),
                      Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        Text(dateText, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w600, color: _ink)),
                        const SizedBox(height: 3),
                        Text('$weekday · ${_calendarExpanded ? '收起月历' : '展开月历'}', style: const TextStyle(fontSize: 13, color: _secondary)),
                      ])),
                      Icon(_calendarExpanded ? Icons.expand_less_rounded : Icons.expand_more_rounded, color: _secondary, size: 20),
                    ])),
                  )),
                  const SizedBox(height: 8),
                  Wrap(spacing: 8, runSpacing: 0, children: [
                    for (final option in _dates) _shortcut(option.label, _dateShortcut == option.label && isSameCalendarDay(_date, option.date),
                      () => setState(() { _date = option.date; _dateShortcut = option.label; }), ValueKey('due-date-${option.label}')),
                  ]),
                  AnimatedSize(
                    duration: MediaQuery.disableAnimationsOf(context) ? Duration.zero : const Duration(milliseconds: 150),
                    alignment: Alignment.topCenter,
                    curve: Curves.easeOut,
                    onEnd: _revealCalendar,
                    child: _calendarExpanded ? Padding(
                      key: _calendarAnchor,
                      padding: const EdgeInsets.only(top: 12),
                      child: Theme(
                        data: Theme.of(context).copyWith(
                          colorScheme: Theme.of(context).colorScheme.copyWith(
                            primary: _accent, onPrimary: Colors.white, primaryContainer: _selection,
                            onPrimaryContainer: _accent, surface: _surface, onSurface: _ink, onSurfaceVariant: _secondary),
                        ),
                        child: DueDateCalendar(
                          key: const ValueKey('due-inline-calendar'),
                          selectedDate: _date,
                          today: now,
                          firstDate: DateTime(math.min(2020, widget.initialValue?.year ?? now.year)),
                          lastDate: DateTime(math.max(now.year + 15, widget.initialValue?.year ?? now.year), 12, 31),
                          onChanged: (day) => setState(() { _date = day; _updateDateShortcut(); }),
                          onDone: _chooseDate,
                        ),
                      ),
                    ) : const SizedBox.shrink(),
                  ),
                  Offstage(offstage: _calendarExpanded, child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                  const SizedBox(height: 18),
                  const Row(children: [
                    Expanded(child: Text('时间', style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600, color: _ink))),
                    Text('24 小时制', style: TextStyle(fontSize: 12, color: _secondary)),
                  ]),
                  const SizedBox(height: 10),
                  Container(padding: const EdgeInsets.fromLTRB(12, 12, 12, 8),
                    decoration: BoxDecoration(color: _surface, borderRadius: BorderRadius.circular(16)),
                    child: Column(children: [
                      const Row(children: [
                        Expanded(child: Center(child: Text('小时', style: TextStyle(fontSize: 12, color: _secondary)))),
                        SizedBox(width: 20),
                        Expanded(child: Center(child: Text('分钟', style: TextStyle(fontSize: 12, color: _secondary)))),
                      ]),
                      SizedBox(height: extent * 3, child: Stack(alignment: Alignment.center, children: [
                        Container(height: extent, decoration: BoxDecoration(color: _selection, borderRadius: BorderRadius.circular(10))),
                        Row(children: [
                          Expanded(child: _wheel(hour: true, extent: extent)),
                          const SizedBox(width: 20, child: Center(child: Text(':', style: TextStyle(fontSize: 30, color: _accent)))),
                          Expanded(child: _wheel(hour: false, extent: extent)),
                        ]),
                      ])),
                    ]),
                  ),
                  const SizedBox(height: 10),
                  Wrap(spacing: 8, runSpacing: 0, children: [
                    for (final time in [(9, 0), (12, 0), (18, 0), (21, 0), (23, 59)])
                      _shortcut('${time.$1.toString().padLeft(2, '0')}:${time.$2.toString().padLeft(2, '0')}',
                        _hour == time.$1 && _minute == time.$2, () => _chooseTime(time.$1, time.$2),
                        ValueKey('due-time-${time.$1}-${time.$2}'), compact: true),
                  ]),
                    ],
                  )),
                ]),
              )),
              Container(
                key: const ValueKey('due-footer'),
                padding: const EdgeInsets.fromLTRB(20, 12, 20, 14),
                decoration: const BoxDecoration(border: Border(top: BorderSide(color: Color(0xFFE8EBF0)))),
                child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                  Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Icon(past ? Icons.history_rounded : Icons.schedule_rounded, size: 18,
                        color: past ? const Color(0xFF946200) : _accent),
                    const SizedBox(width: 8),
                    Expanded(child: Text(summary, key: const ValueKey('due-summary'),
                        style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600, color: _ink))),
                  ]),
                  if (past) const Padding(padding: EdgeInsets.only(top: 4, left: 26),
                    child: Text('此时间已过去', style: TextStyle(fontSize: 12, color: Color(0xFF946200)))),
                  const SizedBox(height: 12),
                  Row(children: [
                    TextButton(
                      key: const ValueKey('due-secondary'),
                      onPressed: () => Navigator.pop(context, _canClear ? const DueDateChoice(null) : null),
                      style: TextButton.styleFrom(foregroundColor: _canClear ? const Color(0xFFB43D37) : _secondary,
                          minimumSize: const Size(64, 48), padding: const EdgeInsets.symmetric(horizontal: 14)),
                      child: Text(_canClear ? '清除' : '取消', style: const TextStyle(fontSize: 15)),
                    ),
                    const SizedBox(width: 12),
                    Expanded(child: FilledButton(
                      key: const ValueKey('due-save'),
                      onPressed: () => Navigator.pop(context, DueDateChoice(_value)),
                      style: FilledButton.styleFrom(backgroundColor: _accent, foregroundColor: Colors.white,
                          minimumSize: const Size(0, 48), padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12))),
                      child: const Text('保存时间', style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
                    )),
                  ]),
                ]),
              ),
            ],
          )),
        ),
      ),
    );
  }
}

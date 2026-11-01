import 'dart:math' as math;
import 'package:flutter/material.dart';

enum _CalendarView { days, months, years }

/// Inline date selection. Browsing months/years never changes the chosen date.
class DueDateCalendar extends StatefulWidget {
  final DateTime selectedDate;
  final DateTime today;
  final DateTime firstDate;
  final DateTime lastDate;
  final ValueChanged<DateTime> onChanged;
  final VoidCallback onDone;

  const DueDateCalendar({super.key, required this.selectedDate, required this.today,
    required this.firstDate, required this.lastDate, required this.onChanged, required this.onDone});

  @override
  State<DueDateCalendar> createState() => _DueDateCalendarState();
}

class _DueDateCalendarState extends State<DueDateCalendar> {
  static const _weekdays = ['一', '二', '三', '四', '五', '六', '日'];
  late DateTime _month;
  late int _year;
  _CalendarView _view = _CalendarView.days;

  @override
  void initState() {
    super.initState();
    _month = DateTime(widget.selectedDate.year, widget.selectedDate.month);
    _year = _month.year;
  }

  @override
  void didUpdateWidget(DueDateCalendar oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!DateUtils.isSameDay(oldWidget.selectedDate, widget.selectedDate)) {
      _month = DateTime(widget.selectedDate.year, widget.selectedDate.month);
      _year = _month.year;
      _view = _CalendarView.days;
    }
  }

  bool _allowed(DateTime day) {
    final date = DateUtils.dateOnly(day);
    return !date.isBefore(DateUtils.dateOnly(widget.firstDate)) &&
        !date.isAfter(DateUtils.dateOnly(widget.lastDate));
  }

  bool _monthAllowed(DateTime month) =>
      !DateTime(month.year, month.month + 1, 0).isBefore(widget.firstDate) &&
      !DateTime(month.year, month.month).isAfter(widget.lastDate);

  void _moveMonth(int delta) {
    final target = DateTime(_month.year, _month.month + delta);
    if (_monthAllowed(target)) setState(() { _month = target; _year = target.year; });
  }

  void _select(DateTime day) {
    if (!_allowed(day)) return;
    setState(() { _month = DateTime(day.year, day.month); _year = day.year; _view = _CalendarView.days; });
    widget.onChanged(day);
  }

  Widget _header(ColorScheme colors) {
    if (_view == _CalendarView.days) {
      return Row(children: [
        Expanded(child: TextButton(
          key: const ValueKey('due-month-heading'),
          onPressed: () => setState(() { _year = _month.year; _view = _CalendarView.months; }),
          style: TextButton.styleFrom(foregroundColor: colors.onSurface,
              minimumSize: const Size(0, 48), padding: const EdgeInsets.symmetric(horizontal: 8)),
          child: Row(children: [
            Flexible(child: FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft,
                child: Text('${_month.year}年${_month.month}月', maxLines: 1,
                  style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)))),
            const SizedBox(width: 4),
            Icon(Icons.expand_more_rounded, size: 18, color: colors.onSurfaceVariant),
          ]),
        )),
        IconButton(key: const ValueKey('due-previous-month'), tooltip: '上个月',
            onPressed: _monthAllowed(DateTime(_month.year, _month.month - 1)) ? () => _moveMonth(-1) : null,
            icon: const Icon(Icons.chevron_left_rounded)),
        IconButton(key: const ValueKey('due-next-month'), tooltip: '下个月',
            onPressed: _monthAllowed(DateTime(_month.year, _month.month + 1)) ? () => _moveMonth(1) : null,
            icon: const Icon(Icons.chevron_right_rounded)),
      ]);
    }
    return Row(children: [
      IconButton(key: const ValueKey('due-calendar-back'),
          tooltip: _view == _CalendarView.years ? '返回月份' : '返回月历',
          onPressed: () => setState(() => _view = _view == _CalendarView.years ? _CalendarView.months : _CalendarView.days),
          icon: const Icon(Icons.arrow_back_rounded, size: 20)),
      Expanded(child: _view == _CalendarView.years
          ? const Text('选择年份', style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600))
          : TextButton(key: const ValueKey('due-year-heading'),
              onPressed: () => setState(() => _view = _CalendarView.years),
              style: TextButton.styleFrom(foregroundColor: colors.onSurface, minimumSize: const Size(0, 48)),
              child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [
                Flexible(child: FittedBox(fit: BoxFit.scaleDown,
                    child: Text('$_year年', maxLines: 1,
                        style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)))),
                const SizedBox(width: 4), const Icon(Icons.expand_more_rounded, size: 18),
              ]))),
      if (_view == _CalendarView.months) ...[
        IconButton(tooltip: '上一年', onPressed: _year > widget.firstDate.year ? () => setState(() => _year--) : null,
            icon: const Icon(Icons.chevron_left_rounded)),
        IconButton(tooltip: '下一年', onPressed: _year < widget.lastDate.year ? () => setState(() => _year++) : null,
            icon: const Icon(Icons.chevron_right_rounded)),
      ],
    ]);
  }

  Widget _days(ColorScheme colors, double cellHeight) {
    final start = DateTime(_month.year, _month.month, 1 - (_month.weekday - 1));
    return Column(children: [
      Padding(padding: const EdgeInsets.symmetric(vertical: 8), child: Row(children: [
        for (var i = 0; i < 7; i++) Expanded(child: Center(child: Text(_weekdays[i],
            style: TextStyle(fontSize: 12, color: colors.onSurfaceVariant, fontWeight: FontWeight.w500)))),
      ])),
      LayoutBuilder(builder: (context, constraints) {
        final markerSize = math.min(constraints.maxWidth / 7 - 4, cellHeight - 4);
        return GridView.builder(
          key: const ValueKey('due-day-grid'),
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          padding: EdgeInsets.zero,
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(crossAxisCount: 7, mainAxisExtent: cellHeight),
          itemCount: 42,
          itemBuilder: (context, index) {
            final day = DateTime(start.year, start.month, start.day + index);
            final selected = DateUtils.isSameDay(day, widget.selectedDate);
            final today = DateUtils.isSameDay(day, widget.today);
            final enabled = _allowed(day);
            final currentMonth = day.month == _month.month && day.year == _month.year;
            final foreground = selected ? colors.onPrimary : !enabled
                ? colors.onSurface.withValues(alpha: .3) : currentMonth ? colors.onSurface : colors.onSurfaceVariant;
            return Semantics(
              button: true, enabled: enabled, selected: selected,
              label: '${day.year}年${day.month}月${day.day}日，星期${_weekdays[day.weekday - 1]}${today ? '，今天' : ''}',
              child: InkWell(
                key: ValueKey('due-day-${day.year}-${day.month}-${day.day}'),
                borderRadius: BorderRadius.circular(10), onTap: enabled ? () => _select(day) : null,
                child: Center(child: Container(
                  width: markerSize, height: markerSize,
                  decoration: BoxDecoration(
                    color: selected ? colors.primary : Colors.transparent,
                    borderRadius: BorderRadius.circular(10),
                    border: today && !selected ? Border.all(color: colors.primary, width: 1.5) : null,
                  ),
                  child: Stack(alignment: Alignment.center, children: [
                    Padding(padding: const EdgeInsets.symmetric(horizontal: 3), child: ExcludeSemantics(
                      child: FittedBox(fit: BoxFit.scaleDown, child: Text('${day.day}',
                        style: TextStyle(fontSize: 16, color: foreground,
                          fontFeatures: const [FontFeature.tabularFigures()],
                          fontWeight: selected || today ? FontWeight.w700 : FontWeight.w400))),
                    )),
                    if (today && selected) Positioned(bottom: 3, child: Container(width: 3, height: 3,
                        decoration: BoxDecoration(color: colors.onPrimary, shape: BoxShape.circle))),
                  ]),
                )),
              ),
            );
          },
        );
      }),
    ]);
  }

  Widget _months(ColorScheme colors, double scale) => GridView.builder(
    key: const ValueKey('due-month-grid'),
    shrinkWrap: true, physics: const NeverScrollableScrollPhysics(), padding: const EdgeInsets.symmetric(vertical: 8),
    gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(crossAxisCount: 3, mainAxisExtent: 48 * scale),
    itemCount: 12,
    itemBuilder: (context, index) {
      final month = index + 1;
      final active = _month.year == _year && _month.month == month;
      return Padding(padding: const EdgeInsets.all(4), child: TextButton(
        key: ValueKey('due-month-$month'),
        onPressed: _monthAllowed(DateTime(_year, month)) ? () => setState(() { _month = DateTime(_year, month); _view = _CalendarView.days; }) : null,
        style: TextButton.styleFrom(foregroundColor: active ? colors.primary : colors.onSurface,
          backgroundColor: active ? colors.primaryContainer : null,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10))),
        child: FittedBox(fit: BoxFit.scaleDown, child: Text('$month月', style: const TextStyle(fontSize: 15))),
      ));
    },
  );

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final scale = (MediaQuery.textScalerOf(context).scale(16) / 16).clamp(1.0, 1.6);
    return Material(
      color: colors.surface,
      borderRadius: BorderRadius.circular(14),
      clipBehavior: Clip.antiAlias,
      child: Column(children: [
        Padding(padding: const EdgeInsets.symmetric(horizontal: 4), child: _header(colors)),
        if (_view == _CalendarView.days) _days(colors, 44 * scale),
        if (_view == _CalendarView.months) _months(colors, scale),
        if (_view == _CalendarView.years) SizedBox(height: 240 * scale,
          child: YearPicker(
            key: const ValueKey('due-year-grid'),
            currentDate: widget.today,
            firstDate: widget.firstDate,
            lastDate: widget.lastDate,
            selectedDate: DateTime(_year, _month.month),
            onChanged: (value) => setState(() { _year = value.year; _view = _CalendarView.months; }),
          )),
        const Divider(height: 1, indent: 12, endIndent: 12),
        Padding(padding: const EdgeInsets.all(8), child: Row(children: [
          Expanded(child: TextButton(key: const ValueKey('due-calendar-today'),
            onPressed: _allowed(widget.today) ? () => _select(DateUtils.dateOnly(widget.today)) : null,
            style: TextButton.styleFrom(foregroundColor: colors.primary, minimumSize: const Size(0, 48)),
            child: const Text('回到今天'))),
          const SizedBox(width: 8),
          Expanded(child: TextButton(key: const ValueKey('due-calendar-done'), onPressed: widget.onDone,
            style: TextButton.styleFrom(foregroundColor: colors.primary, backgroundColor: colors.primaryContainer,
              minimumSize: const Size(0, 48), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10))),
            child: const Text('选择时间'))),
        ])),
      ]),
    );
  }
}

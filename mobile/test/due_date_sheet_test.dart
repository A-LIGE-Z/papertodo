import 'dart:io';
import 'dart:ui' as ui;
import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/due_date_sheet.dart';

final _capture = GlobalKey();
final _now = DateTime(2026, 10, 31, 10, 30);
String? _font;

Future<void> openSheet(WidgetTester tester, {DateTime? initial,
    Size size = const Size(384, 844), double scale = 1,
    String text = '整理下周计划，确认需要推进的几件事',
    ValueChanged<DueDateChoice?>? onResult}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  await tester.pumpWidget(MaterialApp(
    locale: const Locale('zh', 'CN'),
    supportedLocales: const [Locale('zh', 'CN')],
    localizationsDelegates: const [GlobalMaterialLocalizations.delegate,
      GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
    theme: ThemeData(useMaterial3: true, fontFamily: _font,
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF3482FF))),
    builder: (context, child) => MediaQuery(
      data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(scale), padding: const EdgeInsets.only(bottom: 24)),
      child: CupertinoTheme(data: CupertinoThemeData(textTheme: CupertinoTextThemeData(
        pickerTextStyle: TextStyle(fontFamily: _font, fontSize: 32))),
        child: RepaintBoundary(key: _capture, child: child!)),
    ),
    home: Builder(builder: (context) => Scaffold(backgroundColor: const Color(0xFFF0F2F6),
      body: Center(child: FilledButton(key: const ValueKey('open'), onPressed: () async {
        final result = await showDueDateSheet(context: context, taskText: text, initialValue: initial, clock: () => _now);
        onResult?.call(result);
      }, child: const Text('设置待办时间'))))),
  ));
  await tester.tap(find.byKey(const ValueKey('open')));
  await tester.pumpAndSettle();
}

void main() {
  setUpAll(() async {
    if (Platform.environment['DUE_SHEET_SCREENSHOTS'] != null) {
      _font = 'DueSheetQA';
      final bytes = await File('C:/Windows/Fonts/msyh.ttc').readAsBytes();
      await (FontLoader(_font!)..addFont(Future.value(ByteData.sublistView(bytes)))).load();
      final icons = await File('C:/tools/flutter/bin/cache/artifacts/material_fonts/MaterialIcons-Regular.otf').readAsBytes();
      await (FontLoader('MaterialIcons')..addFont(Future.value(ByteData.sublistView(icons)))).load();
    }
  });

  test('Tomorrow is correct at month and year boundaries', () {
    expect(shortDueDate(DateTime(2026, 11, 1), _now), '明天');
    expect(shortDueDate(DateTime(2027, 1, 1), DateTime(2026, 12, 31)), '明天');
  });

  testWidgets('Date and time shortcuts save a complete next-month value', (tester) async {
    DueDateChoice? result;
    await openSheet(tester, onResult: (value) => result = value);
    await tester.tap(find.byKey(const ValueKey('due-date-明天')));
    await tester.ensureVisible(find.byKey(const ValueKey('due-time-23-59')));
    await tester.tap(find.byKey(const ValueKey('due-time-23-59')));
    await tester.pumpAndSettle();
    expect(tester.widget<Text>(find.byKey(const ValueKey('due-summary'))).data, '明天 23:59');
    await tester.tap(find.byKey(const ValueKey('due-save')));
    await tester.pumpAndSettle();
    expect(result?.value, DateTime(2026, 11, 1, 23, 59));
  });

  testWidgets('Overlapping date shortcuts have one selected state', (tester) async {
    await openSheet(tester);
    await tester.tap(find.byKey(const ValueKey('due-date-下周一')));
    await tester.pumpAndSettle();
    expect(tester.widget<ChoiceChip>(find.byKey(const ValueKey('due-date-下周一'))).selected, isTrue);
    expect(tester.widget<ChoiceChip>(find.byKey(const ValueKey('due-date-后天'))).selected, isFalse);
  });

  testWidgets('Existing arbitrary minutes survive opening and saving', (tester) async {
    DueDateChoice? result;
    final initial = DateTime(2026, 11, 4, 9, 7);
    await openSheet(tester, initial: initial, onResult: (value) => result = value);
    await tester.tap(find.byKey(const ValueKey('due-save')));
    await tester.pumpAndSettle();
    expect(result?.value, initial);
  });

  testWidgets('Cancel and clearing an existing due date remain distinct', (tester) async {
    DueDateChoice? result = DueDateChoice(_now);
    await openSheet(tester, initial: _now, onResult: (value) => result = value);
    await tester.tap(find.byKey(const ValueKey('due-close')));
    await tester.pumpAndSettle();
    expect(result, isNull);
    await tester.tap(find.byKey(const ValueKey('open')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-secondary')));
    await tester.pumpAndSettle();
    expect(result, isNotNull);
    expect(result!.value, isNull);
  });

  testWidgets('Inline calendar keeps existing far-future dates without another dialog', (tester) async {
    DueDateChoice? result;
    await openSheet(tester, initial: DateTime(2045, 2, 7, 8, 12), onResult: (value) => result = value);
    await tester.tap(find.byKey(const ValueKey('due-calendar')));
    await tester.pumpAndSettle();
    expect(find.byType(DatePickerDialog), findsNothing);
    expect(find.byKey(const ValueKey('due-inline-calendar')), findsOneWidget);
    expect(find.text('2045年2月'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.ensureVisible(find.byKey(const ValueKey('due-calendar-done')));
    await tester.tap(find.byKey(const ValueKey('due-calendar-done')));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('due-inline-calendar')), findsNothing);
    await tester.tap(find.byKey(const ValueKey('due-save')));
    await tester.pumpAndSettle();
    expect(result?.value, DateTime(2045, 2, 7, 8, 12));
  });

  testWidgets('Browsing across a year does not change the date until a day is picked', (tester) async {
    DueDateChoice? result;
    await openSheet(tester, initial: DateTime(2026, 12, 31, 9, 7), onResult: (value) => result = value);
    await tester.tap(find.byKey(const ValueKey('due-calendar')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-next-month')));
    await tester.pumpAndSettle();
    expect(find.text('2027年1月'), findsOneWidget);
    expect(tester.widget<Text>(find.byKey(const ValueKey('due-summary'))).data, '12月31日 09:07');
    await tester.ensureVisible(find.byKey(const ValueKey('due-day-2027-1-2')));
    await tester.tap(find.byKey(const ValueKey('due-day-2027-1-2')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-save')));
    await tester.pumpAndSettle();
    expect(result?.value, DateTime(2027, 1, 2, 9, 7));
  });

  testWidgets('Leap day can be selected with the original minute preserved', (tester) async {
    DueDateChoice? result;
    await openSheet(tester, initial: DateTime(2028, 1, 30, 12, 34), onResult: (value) => result = value);
    await tester.tap(find.byKey(const ValueKey('due-calendar')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-next-month')));
    await tester.pumpAndSettle();
    final leapDay = find.byKey(const ValueKey('due-day-2028-2-29'));
    await tester.ensureVisible(leapDay);
    await tester.tap(leapDay);
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-save')));
    await tester.pumpAndSettle();
    expect(result?.value, DateTime(2028, 2, 29, 12, 34));
  });

  testWidgets('Year and month navigation leads to a selectable month', (tester) async {
    await openSheet(tester, initial: DateTime(2026, 10, 31, 18, 7));
    await tester.tap(find.byKey(const ValueKey('due-calendar')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-month-heading')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-year-heading')));
    await tester.pumpAndSettle();
    final year = find.text(MaterialLocalizations.of(tester.element(find.byType(YearPicker))).formatYear(DateTime(2027)));
    await tester.ensureVisible(year);
    await tester.tap(year);
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('due-month-2')));
    await tester.pumpAndSettle();
    expect(find.text('2027年2月'), findsOneWidget);
    expect(tester.widget<Text>(find.byKey(const ValueKey('due-summary'))).data, '今天 18:07');
  });

  testWidgets('Return to today changes only the calendar date', (tester) async {
    DueDateChoice? result;
    await openSheet(tester, initial: DateTime(2045, 2, 7, 8, 12), onResult: (value) => result = value);
    await tester.tap(find.byKey(const ValueKey('due-calendar')));
    await tester.pumpAndSettle();
    final today = find.byKey(const ValueKey('due-calendar-today'));
    await tester.ensureVisible(today);
    await tester.tap(today);
    await tester.pumpAndSettle();
    expect(find.text('2026年10月'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('due-save')));
    await tester.pumpAndSettle();
    expect(result?.value, DateTime(2026, 10, 31, 8, 12));
  });

  for (final scenario in [
    (name: 'regular', size: const Size(384, 844), scale: 1.0),
    (name: 'large-text', size: const Size(320, 568), scale: 1.6),
    (name: 'landscape', size: const Size(740, 360), scale: 1.0),
  ]) {
    testWidgets('Footer stays available on ${scenario.name}', (tester) async {
      DueDateChoice? result;
      await openSheet(tester, initial: DateTime(2026, 11, 1, 18, 7),
          size: scenario.size, scale: scenario.scale,
          text: '整理下周计划，确认需要推进的事项，并给重要工作留出充足的时间',
          onResult: (value) => result = value);
      expect(tester.takeException(), isNull);
      final saveRect = tester.getRect(find.byKey(const ValueKey('due-save')));
      expect(saveRect.bottom, lessThanOrEqualTo(scenario.size.height - 24));
      expect(saveRect.top, greaterThanOrEqualTo(0));
      await tester.tap(find.byKey(const ValueKey('due-calendar')));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      expect(tester.getRect(find.byKey(const ValueKey('due-save'))).bottom, lessThanOrEqualTo(scenario.size.height - 24));
      if (scenario.name == 'regular') {
        expect(tester.getRect(find.byKey(const ValueKey('due-calendar-done'))).bottom,
            lessThanOrEqualTo(tester.getRect(find.byKey(const ValueKey('due-footer'))).top));
      }
      final output = Platform.environment['DUE_SHEET_SCREENSHOTS'];
      if (output != null) {
        final boundary = _capture.currentContext!.findRenderObject() as RenderRepaintBoundary;
        await tester.runAsync(() async {
          final image = await boundary.toImage(pixelRatio: 2);
          final data = await image.toByteData(format: ui.ImageByteFormat.png);
          await Directory(output).create(recursive: true);
          await File('$output/${scenario.name}.png').writeAsBytes(data!.buffer.asUint8List());
          image.dispose();
        });
      }
      await tester.ensureVisible(find.byKey(const ValueKey('due-month-heading')));
      await tester.tap(find.byKey(const ValueKey('due-month-heading')));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      await tester.tap(find.byKey(const ValueKey('due-year-heading')));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      expect(find.byType(YearPicker), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('due-save')));
      await tester.pumpAndSettle();
      expect(result?.value, DateTime(2026, 11, 1, 18, 7));
    });
  }
}

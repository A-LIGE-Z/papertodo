import 'dart:convert';
import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:path_provider_platform_interface/path_provider_platform_interface.dart';
import 'package:mobile/main.dart';
import 'package:mobile/models.dart';

class WidgetTestPaths extends PathProviderPlatform {
  final String path;
  WidgetTestPaths(this.path);
  @override
  Future<String?> getApplicationDocumentsPath() async => path;
}

Future<void> finishLoading(WidgetTester tester) async {
  for (var i = 0; i < 100; i++) {
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 20)));
    await tester.pump();
    if (find.byType(CircularProgressIndicator).evaluate().isEmpty) return;
  }
  fail('Local data did not finish loading');
}

void main() {
  late Directory directory;
  late PathProviderPlatform previous;
  setUp(() async {
    directory = await Directory.systemTemp.createTemp('papertodo-widget-test-');
    previous = PathProviderPlatform.instance;
    PathProviderPlatform.instance = WidgetTestPaths(directory.path);
  });
  tearDown(() async {
    PathProviderPlatform.instance = previous;
    await directory.delete(recursive: true);
  });

  testWidgets('The real home screen loads saved papers', (tester) async {
    await tester.runAsync(() => File('${directory.path}/data.json').writeAsString(jsonEncode(AppState(
      papers: [PaperData(id: 'paper', title: '测试纸片', items: [PaperItem(id: 'item', text: '已有待办')])],
    ).toJson())));
    await tester.pumpWidget(const PaperTodoApp());
    await finishLoading(tester);
    expect(find.text('已有待办'), findsWidgets);
    expect(find.text('本地数据暂时无法读取'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('Corrupt data shows recovery state instead of welcome papers', (tester) async {
    const corrupt = '{"papers":[{"content":"recoverable"';
    final file = File('${directory.path}/data.json');
    await tester.runAsync(() => file.writeAsString(corrupt));
    await tester.pumpWidget(const PaperTodoApp());
    await finishLoading(tester);
    expect(find.text('本地数据暂时无法读取'), findsOneWidget);
    expect(find.text('重试'), findsOneWidget);
    expect(find.text('欢迎使用 PaperTodo 手机端'), findsNothing);
    expect(await tester.runAsync(() => file.readAsString()), corrupt);
    await tester.pumpWidget(const SizedBox.shrink());
  });
}

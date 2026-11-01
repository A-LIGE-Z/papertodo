import 'dart:convert';
import 'dart:io';
import 'package:mobile/models.dart';

void main(List<String> arguments) {
  final state = SyncJson.parse(File(arguments[0]).readAsStringSync());
  final note = state.papers.singleWhere((p) => p.id == 'note');
  final item = state.papers.singleWhere((p) => p.id == 'todo').items.singleWhere((i) => i.id == 'item');
  final now = DateTime.now().millisecondsSinceEpoch;
  item.done = true;
  item.updatedAt = now > item.updatedAt ? now : item.updatedAt + 1;
  note.content += '\nEdited on Android';
  note.updatedAt = now > note.updatedAt ? now : note.updatedAt + 1;
  final output = jsonEncode(SyncJson.document(state));
  if (output.contains('syncCustomToken') || output.contains('syncS3SecretKey')) {
    throw StateError('Credentials must not cross the content protocol');
  }
  File(arguments[1]).writeAsStringSync(output);
}

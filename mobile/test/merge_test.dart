import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/models.dart';

void main() {
  test('Dart DataMergeEngine Scenario 1: Non-conflicting edits', () {
    final baseTime = DateTime.now().millisecondsSinceEpoch - 100000;

    final local = AppState(
      papers: [
        PaperData(
          id: 'p1',
          title: 'Todos',
          updatedAt: baseTime + 100,
          items: [
            PaperItem(id: 'i1', text: 'Task 1', done: true, updatedAt: baseTime + 200),
            PaperItem(id: 'i2', text: 'Task 2', done: false, updatedAt: baseTime),
          ],
        ),
      ],
    );

    final remote = AppState(
      papers: [
        PaperData(
          id: 'p1',
          title: 'Todos',
          updatedAt: baseTime + 150,
          items: [
            PaperItem(id: 'i1', text: 'Task 1', done: false, updatedAt: baseTime),
            PaperItem(id: 'i2', text: 'Task 2 edited on PC', done: false, updatedAt: baseTime + 300),
          ],
        ),
      ],
    );

    final res = DataMergeEngine.merge(local, remote);
    expect(res.mergedState.papers.length, 1);
    final p = res.mergedState.papers.first;
    expect(p.items.length, 2);

    final i1 = p.items.firstWhere((i) => i.id == 'i1');
    final i2 = p.items.firstWhere((i) => i.id == 'i2');

    expect(i1.done, true); // Local was newer
    expect(i2.text, 'Task 2 edited on PC'); // Remote was newer
  });

  test('Dart DataMergeEngine Scenario 2: Tombstone deletion', () {
    final baseTime = DateTime.now().millisecondsSinceEpoch - 100000;

    final local = AppState(
      papers: [
        PaperData(
          id: 'p1',
          items: [
            PaperItem(id: 'i1', text: 'Keep me', updatedAt: baseTime),
          ],
        ),
      ],
      syncTombstones: [
        SyncTombstone(id: 'i2', type: 'item', deletedAt: baseTime + 200),
      ],
    );

    final remote = AppState(
      papers: [
        PaperData(
          id: 'p1',
          items: [
            PaperItem(id: 'i1', text: 'Keep me', updatedAt: baseTime),
            PaperItem(id: 'i2', text: 'Delete me', updatedAt: baseTime),
          ],
        ),
      ],
    );

    final res = DataMergeEngine.merge(local, remote);
    final p = res.mergedState.papers.first;
    expect(p.items.length, 1);
    expect(p.items.first.id, 'i1');
  });

  test('Dart DataMergeEngine Scenario 3: Both add papers', () {
    final baseTime = DateTime.now().millisecondsSinceEpoch - 100000;

    final local = AppState(
      papers: [
        PaperData(id: 'p_phone', title: 'Phone Paper', updatedAt: baseTime + 10),
      ],
    );

    final remote = AppState(
      papers: [
        PaperData(id: 'p_pc', title: 'PC Paper', updatedAt: baseTime + 20),
      ],
    );

    final res = DataMergeEngine.merge(local, remote);
    expect(res.mergedState.papers.length, 2);
    expect(res.mergedState.papers.any((p) => p.id == 'p_phone'), true);
    expect(res.mergedState.papers.any((p) => p.id == 'p_pc'), true);
  });
}

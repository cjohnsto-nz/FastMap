# FastMap MapDB Benchmark - foggy-kingdom-coloraccurate

- DB: `C:\Users\chris\AppData\Roaming\VintagestoryData\Maps\5671529e-e893-4f67-86bf-3ab286496c61.db`
- Generated: `2026-05-09T02:09:59.7431383+12:00`

| Stage | Scenario | Operations | Rows/Bytes | Total ms | ms/op | Pages | Detail |
|---|---:|---:|---:|---:|---:|---:|---|
| metadata | db_bytes | 1 | 799985664 | 0.00 | 0.0000 | 0 |  |
| metadata | mappiece_rows | 1 | 65031 | 0.00 | 0.0000 | 0 |  |
| scan_positions | all_rows | 1 | 65031 | 190.67 | 190.6652 | 0 |  |
| metadata | selected_pages | 290 | 65031 | 0.00 | 0.0000 | 0 | totalPages=290 |
| full_scan_decode_group | all_rows | 1 | 65031 | 2090.93 | 2090.9255 | 290 | pixels=66591744;deserialized=65031;blobMs=64.28;deserializeMs=1513.18 |
| point_probe_known_pages | selected_pages | 290 | 65031 | 2295.33 | 7.9149 | 290 | pixels=66591744;deserialized=65031;sqliteQueries=65031;blobMs=75.36;deserializeMs=1320.25;copyMs=7.99 |
| page_in_query | selected_pages | 290 | 65031 | 2000.72 | 6.8990 | 290 | pixels=66591744;deserialized=65031;sqliteQueries=290;blobMs=63.43;deserializeMs=1368.69;copyMs=7.56 |
| page_in_query_parallel | selected_pages | 290 | 65031 | 1331.58 | 4.5917 | 290 | pixels=66591744;deserialized=65031;sqliteQueries=290;blobMs=110.57;deserializeMs=2286.24;copyMs=21.35 |

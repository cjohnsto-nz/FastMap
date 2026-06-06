# FastMap MapDB Benchmark - big-sad-normal

- DB: `C:\Users\chris\AppData\Roaming\VintagestoryData\Maps\406737b8-ec30-402f-8e13-323a0d7647fa.db`
- Generated: `2026-05-09T02:09:44.6969412+12:00`

| Stage | Scenario | Operations | Rows/Bytes | Total ms | ms/op | Pages | Detail |
|---|---:|---:|---:|---:|---:|---:|---|
| metadata | db_bytes | 1 | 513978368 | 0.00 | 0.0000 | 0 |  |
| metadata | mappiece_rows | 1 | 41777 | 0.00 | 0.0000 | 0 |  |
| scan_positions | all_rows | 1 | 41777 | 98.36 | 98.3626 | 0 |  |
| metadata | selected_pages | 208 | 41777 | 0.00 | 0.0000 | 0 | totalPages=208 |
| full_scan_decode_group | all_rows | 1 | 41777 | 1346.36 | 1346.3620 | 208 | pixels=42779648;deserialized=41777;blobMs=43.96;deserializeMs=991.29 |
| point_probe_known_pages | selected_pages | 208 | 41777 | 1468.94 | 7.0622 | 208 | pixels=42779648;deserialized=41777;sqliteQueries=41777;blobMs=54.75;deserializeMs=850.74;copyMs=5.91 |
| page_in_query | selected_pages | 208 | 41777 | 1243.55 | 5.9786 | 208 | pixels=42779648;deserialized=41777;sqliteQueries=208;blobMs=39.83;deserializeMs=877.65;copyMs=5.69 |
| page_in_query_parallel | selected_pages | 208 | 41777 | 724.25 | 3.4820 | 208 | pixels=42779648;deserialized=41777;sqliteQueries=208;blobMs=77.16;deserializeMs=1541.83;copyMs=10.22 |

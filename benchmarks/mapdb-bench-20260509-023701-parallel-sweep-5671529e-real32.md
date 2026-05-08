# FastMap MapDB Benchmark - parallel-sweep-5671529e-real32

- DB: `C:\Users\chris\AppData\Roaming\VintagestoryData\Maps\5671529e-e893-4f67-86bf-3ab286496c61.db`
- Generated: `2026-05-09T02:37:23.3007429+12:00`

| Stage | Scenario | Operations | Rows/Bytes | Total ms | ms/op | Pages | Detail |
|---|---:|---:|---:|---:|---:|---:|---|
| metadata | db_bytes | 1 | 799985664 | 0.00 | 0.0000 | 0 |  |
| metadata | mappiece_rows | 1 | 65031 | 0.00 | 0.0000 | 0 |  |
| scan_positions | all_rows | 1 | 65031 | 187.31 | 187.3118 | 0 |  |
| metadata | selected_pages | 85 | 65031 | 0.00 | 0.0000 | 0 | totalPages=85 |
| full_scan_decode_group | all_rows | 1 | 65031 | 2094.53 | 2094.5298 | 85 | pixels=66591744;deserialized=65031;blobMs=64.09;deserializeMs=1517.89 |
| point_probe_known_pages | selected_pages | 85 | 65031 | 2460.57 | 28.9478 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=65031;blobMs=81.64;deserializeMs=1386.12;copyMs=41.07 |
| page_in_query | selected_pages | 85 | 65031 | 2119.86 | 24.9395 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=63.47;deserializeMs=1385.07;copyMs=42.76 |
| page_in_query_parallel | degree=1 | 85 | 65031 | 2170.38 | 25.5338 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=66.40;deserializeMs=1422.30;copyMs=41.84 |
| page_in_query_parallel | degree=2 | 85 | 65031 | 1976.25 | 23.2500 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=89.81;deserializeMs=2464.72;copyMs=49.25 |
| page_in_query_parallel | degree=3 | 85 | 65031 | 1380.01 | 16.2354 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=97.22;deserializeMs=2109.53;copyMs=47.81 |
| page_in_query_parallel | degree=4 | 85 | 65031 | 1339.51 | 15.7590 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=101.92;deserializeMs=2368.75;copyMs=50.27 |
| page_in_query_parallel | degree=6 | 85 | 65031 | 1308.45 | 15.3935 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=102.71;deserializeMs=2350.81;copyMs=46.74 |
| page_in_query_parallel | degree=8 | 85 | 65031 | 1337.84 | 15.7393 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=120.83;deserializeMs=2322.38;copyMs=47.38 |
| page_in_query_parallel | degree=12 | 85 | 65031 | 1327.72 | 15.6203 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=129.07;deserializeMs=2336.24;copyMs=57.85 |
| page_in_query_parallel | degree=16 | 85 | 65031 | 1338.31 | 15.7448 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=152.66;deserializeMs=2341.85;copyMs=48.10 |
| page_in_query_parallel | degree=24 | 85 | 65031 | 1344.90 | 15.8223 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=108.81;deserializeMs=2364.84;copyMs=56.62 |
| page_in_query_parallel | degree=32 | 85 | 65031 | 1337.82 | 15.7391 | 85 | pixels=66591744;deserialized=65031;sqliteQueries=170;blobMs=124.31;deserializeMs=2357.74;copyMs=50.06 |

# FastMap MapDB Benchmark - parallel-sweep-406737b8-real32

- DB: `C:\Users\chris\AppData\Roaming\VintagestoryData\Maps\406737b8-ec30-402f-8e13-323a0d7647fa.db`
- Generated: `2026-05-09T02:36:49.8127921+12:00`

| Stage | Scenario | Operations | Rows/Bytes | Total ms | ms/op | Pages | Detail |
|---|---:|---:|---:|---:|---:|---:|---|
| metadata | db_bytes | 1 | 513978368 | 0.00 | 0.0000 | 0 |  |
| metadata | mappiece_rows | 1 | 41777 | 0.00 | 0.0000 | 0 |  |
| scan_positions | all_rows | 1 | 41777 | 97.86 | 97.8607 | 0 |  |
| metadata | selected_pages | 64 | 41777 | 0.00 | 0.0000 | 0 | totalPages=64 |
| full_scan_decode_group | all_rows | 1 | 41777 | 1340.66 | 1340.6567 | 64 | pixels=42779648;deserialized=41777;blobMs=42.21;deserializeMs=987.90 |
| point_probe_known_pages | selected_pages | 64 | 41777 | 1465.02 | 22.8909 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=41777;blobMs=51.94;deserializeMs=837.30;copyMs=26.02 |
| page_in_query | selected_pages | 64 | 41777 | 1369.39 | 21.3967 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=42.29;deserializeMs=884.29;copyMs=29.97 |
| page_in_query_parallel | degree=1 | 64 | 41777 | 2746.85 | 42.9195 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=41.31;deserializeMs=2284.53;copyMs=26.58 |
| page_in_query_parallel | degree=2 | 64 | 41777 | 1368.11 | 21.3767 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=57.44;deserializeMs=1827.45;copyMs=32.53 |
| page_in_query_parallel | degree=3 | 64 | 41777 | 909.23 | 14.2067 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=55.01;deserializeMs=1522.07;copyMs=29.74 |
| page_in_query_parallel | degree=4 | 64 | 41777 | 846.81 | 13.2313 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=64.12;deserializeMs=1702.96;copyMs=35.33 |
| page_in_query_parallel | degree=6 | 64 | 41777 | 774.63 | 12.1036 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=65.99;deserializeMs=1654.42;copyMs=31.55 |
| page_in_query_parallel | degree=8 | 64 | 41777 | 801.32 | 12.5207 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=83.78;deserializeMs=1615.93;copyMs=30.85 |
| page_in_query_parallel | degree=12 | 64 | 41777 | 766.30 | 11.9734 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=94.70;deserializeMs=1585.18;copyMs=38.10 |
| page_in_query_parallel | degree=16 | 64 | 41777 | 785.63 | 12.2755 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=72.73;deserializeMs=1560.70;copyMs=36.03 |
| page_in_query_parallel | degree=24 | 64 | 41777 | 777.66 | 12.1510 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=81.68;deserializeMs=1543.24;copyMs=31.92 |
| page_in_query_parallel | degree=32 | 64 | 41777 | 771.73 | 12.0583 | 64 | pixels=42779648;deserialized=41777;sqliteQueries=128;blobMs=78.73;deserializeMs=1534.14;copyMs=32.44 |

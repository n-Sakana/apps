# 第三者ソフトウェアと出典 / Third-party notices

配布物は BAT、PowerShell スクリプト、C# ソースと著作権表示で構成します。
Google の Brotli 復号器を C# ソースで同梱し、Windows 10/11 に含まれる
Windows PowerShell 5.1 と .NET Framework で起動時にコンパイルします。

ファイル形式の読み取り（LevelDB、Chromium IndexedDB、V8 シリアライズ形式、Snappy）は次の公開資料と
公開ソースを参考に C# で独自に実装したもので、コードの複製は含みません。参考にした MIT License の
ソフトウェアについては、その著作権表示と許諾文を下に原文のまま掲げます。

## 参考にした形式文書（BSD-3-Clause のプロジェクトの文書）

- Google LevelDB の形式文書: https://github.com/google/leveldb/blob/main/doc/log_format.md 、 https://github.com/google/leveldb/blob/main/doc/table_format.md 、 https://github.com/google/leveldb/blob/main/doc/impl.md
- Google Snappy の形式文書: https://github.com/google/snappy/blob/main/format_description.txt
- Chromium IndexedDB の LevelDB キー配置: https://chromium.googlesource.com/chromium/src/+/main/content/browser/indexed_db/docs/leveldb_coding_scheme.md
- Chromium IndexedDB scopes（undo ログ）: https://chromium.googlesource.com/chromium/src/+/main/components/services/storage/indexed_db/scopes/README.md
- V8 ValueSerializer: https://chromium.googlesource.com/v8/v8/+/main/src/objects/value-serializer.cc
- Blink SerializedScriptValue / IDBValueWrapper: third_party/blink/renderer/bindings/core/v8/serialization 、 third_party/blink/renderer/modules/indexeddb/idb_value_wrapping.cc

## Microsoft の公開文書

- Microsoft Learn, Deep link to a Teams chat（リンクの形式）: https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/build-and-test/deep-link-teams
- Microsoft Learn, Teams for VDI（新しい Teams の保存先）: https://learn.microsoft.com/en-us/microsoftteams/teams-client-vdi-requirements-deploy

## ccl_chromium_reader（CCL Forensics、MIT License）

https://github.com/cclgroupltd/ccl_chromium_reader

読み取りの手順（ログ／テーブルの走査、IndexedDB の値の包み紙の外し方、V8 の復号、undo ログの解釈）の構成を参考にしました。
以下は同リポジトリの LICENSE の原文です。

```
Copyright 2020, CCL Forensics

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## msteams-local-mcp（Kamorion / KamorionLabs、MIT License）

https://github.com/KamorionLabs/msteams-local-mcp

新しい Teams の IndexedDB（`Teams:replychain-manager:` の `replychains`、`messageMap`）という保存先の知識を参考にしました。
以下は同リポジトリの LICENSE の原文です。

```
MIT License

Copyright (c) 2026 Kamorion / KamorionLabs

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Brotli decoder（Google / the Brotli Authors、MIT License）

`src/brotli/` の C# ソース（`BitReader.cs` から `WordTransformType.cs` までの 15 ファイル）は、Google の brotli リポジトリ（https://github.com/google/brotli、commit 392b261debb0f478811ddacb98b118da36b06158、`csharp/org/brotli/dec/`）の復号器をそのまま収録したものです。痕跡走査（`-Traces`）で、キャッシュ項目の本文を Content-Encoding `br` に従って展開するためだけに使います。改変はしていません。`src/brotli/LICENSE` に原文を同梱しています。

```
Copyright (c) 2009, 2010, 2013-2016 by the Brotli Authors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

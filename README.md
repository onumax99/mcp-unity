# MCP for Unity

Unity エディターの中で動く **MCP (Model Context Protocol) サーバー** です。
Claude Code などの AI アシスタントから、シーン・GameObject・コンポーネント・マテリアル・プレハブの確認や編集、
スクリプトのコンパイルとエラー確認、Play モードの操作、スクリーンショットの取得などができます。

- **Node.js / Python 不要**：Unity エディター内の C# だけで動作します
- **MCP Streamable HTTP** に対応（`http://127.0.0.1:8890/mcp`）
- スクリプトの再コンパイル（ドメインリロード）や Play モード開始後も**自動で再起動**します
- 変更はすべて **Undo 対応**（ツール呼び出し 1 回 = Undo 1 ステップ）
- C# のクラスを 1 つ書くだけで**独自ツールを追加**できます

```
Claude Code ──HTTP (JSON-RPC)──▶ Unity エディター内の MCP サーバー (127.0.0.1:8890)
                                   └─ メインスレッドで Unity API を実行
```

## 動作環境

- Unity 6 (6000.x)。**動作確認は Unity 6000.6.9f1（Windows）で行っています。** ほかのバージョンでは未確認です。
- 依存パッケージ `com.unity.nuget.newtonsoft-json` は自動でインストールされます

## インストール

### 方法 1: Git URL から

Package Manager の **「+」→「Install package from git URL...」** に次を入力します。

```
https://github.com/onumax99/mcp-unity.git?path=/com.yuki.mcp-unity
```

または、プロジェクトの `Packages/manifest.json` の `dependencies` に直接追記します。

```json
"com.yuki.mcp-unity": "https://github.com/onumax99/mcp-unity.git?path=/com.yuki.mcp-unity"
```

### 方法 2: ローカルフォルダーから（パッケージ自体を編集する場合）

このリポジトリを clone してから、次のどちらかで導入します。編集した内容がすぐにプロジェクトへ反映されます。

1. Unity でプロジェクトを開き、**Window > Package Manager** を開く
2. 左上の **「+」→「Install package from disk...」** を選ぶ
3. clone したフォルダーの `com.yuki.mcp-unity/package.json` を選択

または、`Packages/manifest.json` の `dependencies` に clone 先のパスを書きます。

```json
"com.yuki.mcp-unity": "file:<clone したフォルダー>/com.yuki.mcp-unity"
```

### 起動の確認

インストールするとサーバーが自動で起動し、Console に次のように表示されます。

```
[MCP] Server listening on http://127.0.0.1:8890/mcp (26 tools).
```

**Window > MCP for Unity** で状態・ポート・最近の呼び出し履歴を確認できます。

## Claude Code への接続

Unity を起動した状態で、ターミナルで次を実行します（Unity プロジェクトのフォルダーで実行するのがおすすめです）。

```powershell
claude mcp add --transport http unity http://127.0.0.1:8890/mcp
```

- すべてのプロジェクトで使いたい場合は `--scope user` を付けます。
- **Window > MCP for Unity** の **「Write .mcp.json」** ボタンでも設定できます（Unity プロジェクトのルートに `.mcp.json` を作成します。Claude Code をそのフォルダーで起動すると、サーバーの使用許可を求められます）。

Claude Code で `/mcp` を実行し、`unity` が **connected** になっていれば完了です。
Claude Code を先に起動していた場合は、`/mcp` から再接続してください。

### 動作確認スクリプト

```powershell
powershell -ExecutionPolicy Bypass -File tests\smoke-test.ps1
```

## 使い方の例（Claude Code への指示）

- 「今のシーンの構成を教えて」
- 「床に Plane を置いて、赤いマテリアルを作って適用して」
- 「Player を WASD で動かすスクリプトを書いて、Player にアタッチして」
- 「Play モードで実行して、エラーが出ていないか確認して」
- 「Game ビューのスクリーンショットを撮って、見た目を確認して」
- 「Enemy をプレハブにして、x 方向に 2 ずつずらして 5 体並べて」

## ツール一覧（26 個）

| 分類 | ツール | 内容 |
| --- | --- | --- |
| エディター | `get_editor_state` | Unity バージョン、プロジェクト、Play 状態、コンパイル状態、開いているシーン、選択中のオブジェクト、エラー件数 |
| | `play_mode` | Play / Stop / Pause / Resume / 1 フレーム進める |
| | `execute_menu_item` | メニューコマンドを実行（例: `GameObject/3D Object/Cube`） |
| | `set_selection` | オブジェクトやアセットを選択し、Scene ビューでフォーカス |
| | `undo` | Undo / Redo |
| スクリプト | `refresh_assets` | 変更したファイルを取り込み、コンパイルを待ってエラーを返す |
| | `get_compilation_status` | 直近のコンパイル結果（エラー・警告） |
| | `get_console_logs` | Console のログ（種類・キーワード・`sinceId` で絞り込み可） |
| シーン | `get_hierarchy` | ヒエラルキーのツリー |
| | `find_gameobjects` | 名前（ワイルドカード可）・タグ・レイヤー・コンポーネントで検索 |
| | `get_gameobject` | Transform、コンポーネント一覧、親子、プレハブ情報 |
| | `create_gameobject` | 空 / プリミティブ / プレハブから作成（コンポーネント追加も同時に可能） |
| | `modify_gameobject` | 名前、アクティブ、タグ、レイヤー、親、Transform の変更 |
| | `duplicate_gameobject` | 複製（プレハブのリンクを維持、位置をずらして複数作成も可） |
| | `delete_gameobjects` | 削除 |
| | `manage_scene` | シーンの一覧 / 開く / 保存 / 新規作成 / アクティブ化 / 閉じる |
| コンポーネント | `add_component` | コンポーネント追加（初期値の設定も可） |
| | `remove_component` | コンポーネント削除 |
| | `get_properties` | Inspector に表示されるフィールドを読む（コンポーネント・ScriptableObject・ProjectSettings など） |
| | `set_properties` | フィールドを書き込む（数値、ベクトル、色、enum、配列、アセット参照、シーン参照など） |
| アセット | `find_assets` | Project 内を検索（`t:Prefab`、`t:Material` など） |
| | `create_or_update_material` | マテリアルの作成・更新（URP / HDRP / Built-in の既定シェーダーを自動選択） |
| | `create_prefab` | シーンの GameObject をプレハブとして保存 |
| その他 | `screenshot` | カメラ（または Scene ビューの視点）の画像を取得 |
| | `invoke_static_method` | 任意の static メソッドを呼び出す（自作の Editor スクリプトの実行など） |
| | `batch` | 複数のツールを 1 回のリクエストでまとめて実行（Undo も 1 ステップ） |

### 値の指定方法

- **GameObject の指定（target）**：結果に含まれる `id`、ヒエラルキーのパス（`Parent/Child`）、または一意な名前
- **ベクトル**：`[x, y, z]`（`{"x":1,"y":2,"z":3}` や `"1,2,3"` も可）。回転はオイラー角（度）
- **色**：`[r, g, b, a]`（0〜1）、`"#FF8800"`、`"red"` など
- **アセット参照**：`"Assets/Materials/Red.mat"`、サブアセットは `"Assets/Model.fbx::MeshName"`
- **プロパティ名**：`mass` のように書けば `m_Mass` も自動で探します。配列要素は `m_Materials[0]`

## 設定

| 項目 | 場所 / 方法 |
| --- | --- |
| ポート（既定 8890） | Window > MCP for Unity（プロジェクトごとに `UserSettings/` に保存） |
| 自動起動 | Window > MCP for Unity の「Start automatically」 |
| ポートの一時変更 | Unity の起動引数 `-mcpPort 9000` |
| バッチモード | 既定では起動しません。`-mcpServer` 引数か環境変数 `UNITY_MCP_BATCHMODE=1` で起動 |

複数の Unity を同時に開く場合は、プロジェクトごとに別のポートを設定してください。

## 独自ツールの追加

Editor フォルダー（または Editor 用 asmdef）に `McpTool` を継承したクラスを置くだけで、次回のコンパイル後に自動で登録されます。

```csharp
using McpUnity;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class CountEnemiesTool : McpTool
{
    public override string Name => "count_enemies";
    public override string Description => "Count GameObjects tagged 'Enemy' in the open scenes.";
    public override bool ReadOnly => true;

    public override JObject InputSchema => Schema.Object()
        .Bool("includeInactive", "Also count inactive enemies (default false).");

    protected override object Execute(JObject args, ToolContext context)
    {
        bool includeInactive = args.GetBool("includeInactive", false);
        var all = Object.FindObjectsByType<GameObject>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
        int count = System.Linq.Enumerable.Count(all, go => go.CompareTag("Enemy"));
        return new { count };
    }
}
```

- `Execute` は必ずメインスレッドで呼ばれるので、Unity API をそのまま使えます
- 文字列・任意のオブジェクト（JSON に変換）・`ToolResult`（画像付きなど）を返せます
- `McpToolException` を投げると、エラーとして AI に伝わります

## トラブルシューティング

| 症状 | 対処 |
| --- | --- |
| Claude Code から接続できない | Unity が起動しているか、Window > MCP for Unity が **Running** か確認。Claude Code で `/mcp` から再接続 |
| `Could not listen on port 8890` | 他のアプリや別の Unity がポートを使用中。Window > MCP for Unity でポートを変更し、`claude mcp add` も同じポートでやり直す |
| スクリプトを編集しても反映されない | Unity は非アクティブ中にファイル変更を検知しないことがあります。`refresh_assets` を使ってください（AI には自動で案内されます） |
| コンパイル直後に一時的にエラーになる | ドメインリロード中（数秒）です。少し待てば復帰します |
| ツールがタイムアウトする | Unity でダイアログが開いていないか確認してください（モーダルダイアログ中は処理できません） |

## セキュリティについて

- `127.0.0.1`（ローカル）でのみ待ち受け、外部からは接続できません
- `Host` / `Origin` ヘッダーを検証し、ブラウザ経由の攻撃（DNS リバインディング）を防ぎます
- **認証はありません。** 同じ PC 上のプログラムはアクセスできます。`invoke_static_method` や `execute_menu_item` は任意のコードを実行できるため、不要なときは Window > MCP for Unity で **Stop** してください

## 制限事項

- stdio のみ対応のクライアント（Claude Desktop のローカル設定など）からは、`npx mcp-remote http://127.0.0.1:8890/mcp` などのブリッジが必要です
- `screenshot` には Screen Space - Overlay の UI とギズモは写りません
- サーバーからの通知（SSE ストリーム）やリソース / プロンプト機能は未対応です（ツールのみ）

## ファイル構成

```
com.yuki.mcp-unity/        Unity パッケージ本体
  Editor/Api/              公開 API（McpTool、Schema、JsonArgs）
  Editor/Core/             起動・再起動、メインスレッド実行、ログ・コンパイル結果の記録
  Editor/Server/           HTTP サーバーと MCP (JSON-RPC) の処理
  Editor/Tools/            各ツールの実装
  Editor/Util/             値の変換、オブジェクト検索、シリアライズ済みプロパティの読み書き
  Editor/UI/               Window > MCP for Unity
tests/smoke-test.ps1       動作確認スクリプト
```

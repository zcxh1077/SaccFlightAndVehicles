# SaccFlightAndVehicles カスタムアドオン開発

## プロジェクト概要

SaccFlightAndVehicles (SFV) は VRChat 向けの航空機・乗り物スクリプトパッケージ。
このリポジトリでは SFV に独自のアドオンを追加開発している。
作成したアドオンはすべて `WBAddon/` ディレクトリに保存する。

## 技術スタック

- **言語**: C# (UdonSharp)
- **プラットフォーム**: VRChat / Unity
- **スクリプトシステム**: VRChat Udon (UdonSharp で C# 記述)
- **名前空間**: `SaccFlightAndVehicles`

## ディレクトリ構成

```
SaccFlightAndVehicles/     # SFV 本体（変更しない）
WBAddon/                   # カスタムアドオン置き場
  NPCLaserTurret/          # NPC レーザータレット
  SAV_HybridAAMController/ # ハイブリッド AAM コントローラー
```

## コーディング規約

- **コメント・Tooltip は日本語**で記述する
- UdonSharp の制約に従う（ジェネリクス不可、一部 C# 機能が使えない等）
- 各アドオンは独立したディレクトリに配置し、以下の構成を持つ:
  - `Scripts/` - `.cs` スクリプトと `.asset` ファイル
  - `Prefab/`, `Material/`, `Texture/`, `FBX/`, `Animation/`, `Sound/`, `Editor/` 等（必要に応じて）
- SFV の既存クラス（`SaccEntity`, `UdonSharpBehaviour` 等）を継承・参照する
- `[UdonBehaviourSyncMode]` 属性を必ず指定する

## UdonSharp 特有の制約

- ジェネリクス型（`List<T>`, `Dictionary<K,V>` 等）は使用不可 → 配列で代替
- `typeof()` は一部制限あり
- `static` メンバーは使用不可
- 非 Udon コンポーネントへのアクセスは `GetComponent` 経由で行う
- ネットワーク同期変数には `[UdonSynced]` 属性を付ける
- `SendCustomNetworkEvent` でネットワークイベントを送る

## 既存アドオンの概要

### NPCLaserTurret
プレイヤー・航空機を自動追尾して攻撃するNPCレーザータレット。
- 方位角制限、スキャン動作、仰角制限
- ターゲット検出: Raycast ベース
- ネットワーク同期: `BehaviourSyncMode.Manual`

### SAV_HybridAAMController
対空・対地両対応のハイブリッドミサイルコントローラー。

- FOX-1/FOX-3 両対応、PitBull モード
- アスペクト角によるシーカー感度変化
- カウンターメジャー対応

## 開発時の注意

- SFV 本体のファイルは**変更しない**
- `WBAddon/` 内のみ編集・追加する
- Unity の `.meta` ファイルはバイナリではなくテキスト形式（GUID 管理）
- `.asset` ファイルは UdonSharp が自動生成するので手動編集しない

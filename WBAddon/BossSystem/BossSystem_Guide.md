# BossSystem セットアップガイド

SaccFlightAndVehicles (SFV) 向けのマルチパーツボスシステム。
ボス本体HP、弱点コライダー経由のダメージ、パーツ破壊によるボスダメージ、
パーツに紐づくタレットの連動無効化/復活を実現する。

---

## システム概要

### ファイル構成

```
WBAddon/BossSystem/
├── Scripts/
│   ├── BossController.cs      ランタイム: ボスHP管理・死亡/リスポーン制御
│   ├── BossDamageRelay.cs     ランタイム: 弱点パーティクル検出・ダメージ転送
│   └── BossPartLink.cs        ランタイム: パーツ破壊通知・タレット連動
├── Editor/
│   └── BossControllerEditor.cs エディタ: 参照フィールド自動設定・バリデーション
└── BossSystem_Guide.md         本ドキュメント
```

### ランタイムスクリプト

| スクリプト | 役割 | 配置先 |
|---|---|---|
| **BossController** | ボスHP管理・死亡/リスポーン制御 | ボス本体の SaccEntity 子オブジェクト |
| **BossDamageRelay** | 弱点パーティクル検出・ダメージ転送 | 弱点コライダーの GameObject |
| **BossPartLink** | パーツ破壊通知・タレット連動 | 各パーツの SaccTarget 子オブジェクト |

### エディタスクリプト

| スクリプト | 役割 |
|---|---|
| **BossControllerEditor** | BossController Inspector に「ヒエラルキーから自動設定」ボタンを追加。参照登録とバリデーションを一括実行する |

### ダメージフロー

```
┌─────────────────────────────────────────────────┐
│ A: パーティクル → 弱点 → ボスHP減算             │
│                                                 │
│ パーティクル衝突                                │
│   → WeakPoint の BossDamageRelay                │
│     → OnParticleCollision                       │
│       → d:/t: パース → 装甲/倍率適用           │
│         → BossController.ReceiveDamage()        │
│           → [NetworkCallable] SendBossDamageEvent│
│             → オーナーが HP 減算                │
│               → HP <= 0 なら ExplodeBoss        │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│ B: パーツ破壊 → ボスHP減算                      │
│                                                 │
│ SaccTarget.Health <= 0 (オーナー処理)           │
│   → OnDeserialization                           │
│     → ExplodeOther                              │
│       → BossPartLink.Explode()                  │
│         → [オーナーのみ]                        │
│           BossController.ReceiveDamage(パーツHP) │
│         → [全クライアント]                      │
│           子タレット.dead = true                 │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│ C: ボス死亡 → 全体破壊                          │
│                                                 │
│ BossController.Health <= 0                      │
│   → NetworkExplodeBoss                          │
│     → [全クライアント] ExplodeBoss              │
│       → 全パーツ Explode / 全タレット dead      │
│       → RespawnDelay 後 → RespawnBoss           │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│ D: ボスリスポーン                                │
│                                                 │
│ RespawnBoss → HP全回復                          │
│   → 全パーツ RespawnTarget                      │
│   → 全 BossPartLink.OnBossRespawn              │
│     → タレット復活 (dead=false + ReAppear)      │
│   → InvincibleAfterSpawn 後 → 無敵解除         │
└─────────────────────────────────────────────────┘
```

---

## Unityヒエラルキー構成

```
BossRoot/
│
├── BossBody                    ← SaccEntity + Rigidbody + Collider群
│   │                             DisableBulletHitEvent = true (本体直接ダメージ無効)
│   │
│   ├── BossController          ← BossController スクリプト
│   │                             SaccEntity.ExtensionUdonBehaviours に登録
│   │
│   ├── WeakPoint_A             ← BossDamageRelay + Collider + Rigidbody(IsKinematic)
│   ├── WeakPoint_B             ← 同上（複数弱点可）
│   └── ...
│
├── BossPart_1                  ← SaccTarget (HP:1000, RespawnDelay:0)
│   │                             + Animator + Collider
│   │
│   ├── BossPartLink_1          ← BossPartLink スクリプト
│   │                             SaccTarget.ExplodeOther に登録
│   │
│   ├── Turret_1A               ← SaccEntity + NPCLaserTurret (HP:100)
│   └── Turret_1B               ← 同上
│
├── BossPart_2                  ← 同上（パーツ数は自由）
│   ├── BossPartLink_2
│   ├── Turret_2A
│   └── ...
│
└── (VFXやUI等)
```

---

## セットアップ手順

### 概要: 手動設定とエディタ自動設定

ボスのセットアップには2つの段階がある:

1. **ヒエラルキーとコンポーネントの配置** — 手動で行う (手順 1～4)
2. **参照フィールドの登録** — **エディタスクリプトで自動化**できる (手順 5)

手順 1～4 でヒエラルキーを組み立てた後、BossController Inspector の
「ヒエラルキーから自動設定」ボタンを押すだけで、手順 5 の参照登録と
手順 6 のバリデーションが一括で完了する。

### 1. ボス本体の作成

1. 空の GameObject `BossRoot` を作成する
2. 子に `BossBody` を作成し、以下を追加する:
   - **SaccEntity** コンポーネント
   - **Rigidbody** (IsKinematic 推奨)
   - **Collider** (本体の当たり判定)
3. SaccEntity の設定:
   - `DisableBulletHitEvent = true` (本体コライダーへの直接ダメージを無効化)
   - `ArmorStrength` は任意
4. `BossBody` の子に空 GameObject を作成し **BossController** スクリプトを追加する

### 2. 弱点の作成

弱点ごとに以下を行う:

1. `BossBody` の子に `WeakPoint_X` を作成する
2. 以下のコンポーネントを追加する:
   - **Collider** (弱点の当たり判定。Box/Sphere/Capsule等)
   - **Rigidbody** → **Is Kinematic = true** にチェック (**必須**)
   - **BossDamageRelay** スクリプト
3. BossDamageRelay の設定:
   - `ArmorStrength`: 装甲値 (1.0 = 等倍ダメージ、2.0 = 半減)
   - `NoDamageBelow`: この値未満のダメージは無効
   - `DamageMultiplier`: ダメージ倍率 (2.0 = 2倍ダメージの弱点)

> **なぜキネマティック Rigidbody が必要か:**
> Unity では子コライダーの OnParticleCollision は親の Rigidbody に発火する。
> 弱点に独自のキネマティック Rigidbody を付けることで、
> 親の SaccEntity ではなく弱点自体の BossDamageRelay で検出される。

### 3. パーツの作成

パーツごとに以下を行う:

1. `BossRoot` の子に `BossPart_X` を作成する
2. 以下のコンポーネントを追加する:
   - **SaccTarget** コンポーネント
   - **Animator** (hit/dead/healthpc の制御用)
   - **Collider** (パーツの当たり判定)
3. SaccTarget の設定:
   - `Health`: パーツHP (例: 1000)
   - `RespawnDelay = 0` (**必須**: パーツ個別リスポーンを無効化し、BossController が一括管理する)
4. `BossPart_X` の子に空 GameObject を作成し **BossPartLink** スクリプトを追加する
5. BossPartLink の設定（自動設定を使わない場合のみ手動で行う）:
   - `PartTarget`: 親の SaccTarget を参照
   - `DamageOnDestruction`: 破壊時にボスに与えるダメージ (0 = SaccTarget.FullHealth を自動取得)

### 4. タレットの作成

タレットごとに以下を行う:

1. 対応する `BossPart_X` の子に `Turret_XY` を作成する
2. 既存の **NPCLaserTurret** プレハブを配置するか、以下を追加する:
   - **SaccEntity** コンポーネント
   - **NPCLaserTurret** スクリプト (SaccEntity の ExtensionUdonBehaviours に登録)
3. NPCLaserTurret の設定は通常通り行う (HP, 射程, 回転速度等)

> タレットスクリプト自体の変更は不要。
> BossPartLink が `SaccEntity.dead` を操作することで無効化/復活を制御する。

### 5. 参照登録（エディタ自動設定）

ヒエラルキーの配置（手順 1～4）が済んだら、BossController の Inspector 上部にある
「**ヒエラルキーから自動設定**」ボタンを押す。以下が一括で実行される:

| 対象 | 自動設定される内容 |
|---|---|
| **BossController** | `BossParts` / `BossPartLinks` / `TurretEntities` / `DamageRelays` をヒエラルキーから収集して設定 |
| **BossController** | `BossAnimator` を BossBody または BossRoot の Animator から自動検出（未設定時のみ） |
| **BossPartLink** | `PartTarget` を祖先の SaccTarget から自動設定 |
| **BossPartLink** | `ChildTurrets` をパーツ以下のタレット SaccEntity から自動設定 |
| **SaccEntity** | `ExtensionUdonBehaviours` に BossController を追加（重複チェック済み） |
| **SaccTarget** | `ExplodeOther` に対応する BossPartLink を追加（重複チェック済み） |

自動設定完了後、検出数のサマリーと設定不備の警告がダイアログで表示される。

**自動設定後に手動で設定が必要なフィールド:**

| フィールド | 説明 |
|---|---|
| `MaxHealth` | ボスの最大HP (デフォルト: 5000) |
| `RespawnDelay` | ボス死亡後のリスポーン待機秒数 (0 = リスポーンしない) |
| `InvincibleAfterSpawn` | リスポーン後の無敵時間 (秒) |
| `EnableOnDeath` | ボス死亡時に有効化する GameObject (爆発エフェクト等) |
| `DisableOnDeath` | ボス死亡時に無効化する GameObject (本体メッシュ等) |
| `SendKillEvents` / `KillFeed` | キルフィード連携（任意） |
| BossPartLink `DamageOnDestruction` | パーツ破壊時のボスダメージ量（0 = パーツ最大HP、任意） |
| BossPartLink `EnableOnDestroy` / `DisableOnDestroy` | パーツ破壊時の演出 GameObject（任意） |
| BossDamageRelay `ArmorStrength` / `DamageMultiplier` | 弱点ごとの装甲値・倍率（任意） |

> **自動設定の検索ロジック:**
> BossController の祖先 SaccEntity を「ボス本体」として特定し、
> その親（BossRoot）以下を走査する。
> ボス本体以外の SaccEntity はすべてタレットとして収集される。
> BossPartLink は祖先方向に最も近い SaccTarget を自身のパーツとして認識する。

### 6. バリデーション

自動設定の実行時に以下の項目が自動チェックされ、不備があれば警告が表示される:

| チェック項目 | 期待値 | 理由 |
|---|---|---|
| BossBody SaccEntity `DisableBulletHitEvent` | `true` | 本体コライダーへの直接ダメージを無効化するため |
| 各パーツ SaccTarget `RespawnDelay` | `0` | BossController が一括管理するため |
| 各弱点の Rigidbody 有無 | 存在する | OnParticleCollision を受けるために必須 |
| 各弱点の `Rigidbody.isKinematic` | `true` | 親 Rigidbody ではなく弱点自体で検出するため |

手動確認用チェックリスト:

- [ ] BossBody の SaccEntity で `DisableBulletHitEvent = true`
- [ ] 各弱点に **Rigidbody (Is Kinematic = true)** と **Collider** が付いている
- [ ] 各パーツの SaccTarget で `RespawnDelay = 0`
- [ ] 自動設定のダイアログで警告が出ていないこと

---

## Animator パラメータ一覧

BossController が制御する Animator パラメータ:

| パラメータ | 型 | 説明 |
|---|---|---|
| `healthpc` | Float | 現在HP / 最大HP (0.0 ~ 1.0) |
| `dead` | Bool | 死亡状態 |
| `hit` | Trigger | 被弾時に発火 |
| `explode` | Trigger | ボス死亡時に発火 |
| `reappear` | Trigger | リスポーン時に発火 |

パーツの Animator は SaccTarget が標準で制御する (`healthpc`, `dead`, `hit`)。

---

## Inspector プロパティ詳細

### BossController

`[自動]` マークのフィールドはエディタ自動設定で登録される。

| プロパティ | 型 | デフォルト | 説明 |
|---|---|---|---|
| MaxHealth | float | 5000 | ボスの最大HP |
| RespawnDelay | float | 30 | リスポーンまでの秒数。0 = リスポーンしない |
| InvincibleAfterSpawn | float | 3 | リスポーン後の無敵秒数 |
| BossParts `[自動]` | UdonSharpBehaviour[] | - | 全パーツの SaccTarget |
| BossPartLinks `[自動]` | UdonSharpBehaviour[] | - | 全 BossPartLink |
| TurretEntities `[自動]` | SaccEntity[] | - | 全タレットの SaccEntity |
| DamageRelays `[自動]` | UdonSharpBehaviour[] | - | 全 BossDamageRelay |
| BossAnimator `[自動]` | Animator | - | ボス演出用 (未設定時のみ自動検出) |
| EnableOnDeath | GameObject[] | - | 死亡時に有効化 |
| DisableOnDeath | GameObject[] | - | 死亡時に無効化 |
| SendKillEvents | bool | false | キルフィードに通知するか |
| KillFeed | UdonBehaviour | - | キルフィード参照 |
| BossKilledMessages | string[] | - | キルメッセージ (%KILLER% で置換) |

> `EntityControl` は SaccEntity が ExtensionUdonBehaviours 経由で実行時に自動設定する。
> ExtensionUdonBehaviours への登録自体もエディタ自動設定で行われる。

### BossDamageRelay

| プロパティ | 型 | デフォルト | 説明 |
|---|---|---|---|
| ArmorStrength | float | 1 | 装甲値。d:未指定時のデフォルトダメージ = 10 / ArmorStrength |
| NoDamageBelow | float | 0 | この値未満のダメージを無効化 |
| DamageMultiplier | float | 1 | ダメージ倍率。2.0 で2倍ダメージ弱点 |

> `BossController` と `BossEntity` は BossController の SFEXT_L_EntityStart で実行時に自動設定される。Inspector 設定不要。

### BossPartLink

`[自動]` マークのフィールドはエディタ自動設定で登録される。

| プロパティ | 型 | デフォルト | 説明 |
|---|---|---|---|
| PartTarget `[自動]` | UdonSharpBehaviour | - | このパーツの SaccTarget 参照 |
| DamageOnDestruction | float | 0 | 破壊時ボスダメージ。0 = PartTarget.FullHealth を使用 |
| ChildTurrets `[自動]` | SaccEntity[] | - | 連動無効化するタレットの SaccEntity |
| EnableOnDestroy | GameObject[] | - | 破壊時に有効化 |
| DisableOnDestroy | GameObject[] | - | 破壊時に無効化 |

> `BossController` は BossController の SFEXT_L_EntityStart で実行時に自動設定される。Inspector 設定不要。
> SaccTarget.ExplodeOther への BossPartLink 登録もエディタ自動設定で行われる。

---

## ネットワーク同期の仕組み

- **BossController** は `BehaviourSyncMode.Manual` で `Health` を `[UdonSynced]` 同期する
- ダメージは SaccTarget/SaccEntity 準拠の **0.2秒バッチキュー** で送信し、ネットワーク負荷を軽減する
- HP 減算は **オーナーのみ** が実行する (権威サーバーモデル)
- **非オーナーのダメージ予測**: 自分がボスを攻撃した場合、ネットワーク確認を待たずにローカルで爆発を先行表示する。1秒後にネットワーク状態と照合し、不一致なら復元する
- **レイトジョイナー**: `OnDeserialization` で同期済み Health を受信し、死亡状態であれば即座に `ExplodeBoss` を実行する
- **オーナー移譲**: `OnOwnershipTransferred` と `SFEXT_O_TakeOwnership` / `SFEXT_O_LoseOwnership` で追跡する

---

## 設計上の注意点

### パーツ破壊の重複防止

SaccTarget の `ExplodeOther` は `OnDeserialization` 内で呼ばれるため、
全クライアントで `BossPartLink.Explode()` が実行される。
ボスへのダメージ送信は **SaccTarget のオーナーのみ** が行い、重複を防止している。
タレット無効化と演出切替は全クライアントで実行して視覚的一貫性を保つ。

### ボス死亡後のパーツ破壊

`ExplodeBoss` 内で全パーツを強制破壊するため、
既に破壊済みのパーツの `BossPartLink.Explode()` が再度呼ばれる可能性がある。
`partDestroyed` フラグで二重処理を防止している。

### ヒットスキャン武器について

現在の実装はパーティクルベースの武器のみ対応している。
`SAV_HitScanShot` 等のヒットスキャン武器は `GetComponent<SaccTarget>()` でターゲットを検出するため、
BossDamageRelay 単体では対応できない。
ヒットスキャン対応が必要な場合は、弱点にダミー SaccTarget を追加し、
HP 変化を監視して BossController に転送するブリッジスクリプトが必要になる。

---

## 構成例: HP5000 のボス + パーツ2つ + タレット4基

```
HP 構成:
  ボス本体:    5000 HP
  パーツ_1:    1000 HP → 破壊で ボス -1000
  パーツ_2:    1000 HP → 破壊で ボス -1000
  弱点 x2:     DamageMultiplier = 2.0

タレット構成:
  パーツ_1 → Turret_1A (100HP), Turret_1B (100HP)
  パーツ_2 → Turret_2A (100HP), Turret_2B (100HP)

撃破パターン例:
  1. 弱点を撃って 3000 ダメージ蓄積
  2. パーツ_1 を破壊 → ボス -1000 (合計4000) → Turret_1A, 1B 停止
  3. パーツ_2 を破壊 → ボス -1000 (合計5000) → ボス死亡 → 全体爆発
  4. 30秒後にリスポーン → 全パーツ/タレット復活
```

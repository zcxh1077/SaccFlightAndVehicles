
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.SDK3.UdonNetworkCalling;

namespace SaccFlightAndVehicles
{
    /// <summary>
    /// ボスシステムのメインコントローラー。
    /// SaccEntity拡張としてExtensionUdonBehavioursに登録して使用する。
    /// ボスHP管理、弱点/パーツ破壊からのダメージ受信、死亡/リスポーン制御を行う。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class BossController : UdonSharpBehaviour
    {
        [Header("ボスHP設定")]
        [Tooltip("ボスの最大HP")]
        public float MaxHealth = 5000f;
        [UdonSynced] public float Health;

        [Header("リスポーン設定")]
        [Tooltip("ボス死亡後のリスポーン遅延（秒）。0でリスポーンしない")]
        public float RespawnDelay = 30f;
        [Tooltip("リスポーン後の無敵時間（秒）")]
        public float InvincibleAfterSpawn = 3f;

        [Header("サブコンポーネント参照")]
        [Tooltip("全パーツのSaccTarget参照")]
        public UdonSharpBehaviour[] BossParts;
        [Tooltip("全BossPartLink参照")]
        public UdonSharpBehaviour[] BossPartLinks;
        [Tooltip("全タレットのSaccEntity参照")]
        public SaccEntity[] TurretEntities;
        [Tooltip("全BossDamageRelay参照")]
        public UdonSharpBehaviour[] DamageRelays;

        [Header("演出")]
        [Tooltip("ボス演出用Animator")]
        public Animator BossAnimator;
        [Tooltip("死亡時に有効化するGameObject")]
        public GameObject[] EnableOnDeath;
        [Tooltip("死亡時に無効化するGameObject")]
        public GameObject[] DisableOnDeath;

        [Header("キルフィード")]
        [Tooltip("キルイベントを送信するか")]
        public bool SendKillEvents;
        public UdonBehaviour KillFeed;
        [SerializeField] private string[] BossKilledMessages = { "%KILLER% がボスを撃破した", };

        // SaccEntity拡張 — EntityStartで自動設定される
        [System.NonSerialized] public SaccEntity EntityControl;

        // 内部状態
        private VRCPlayerApi localPlayer;
        private bool isOwner;
        private bool dead;
        private bool initialized;

        // ダメージキューイング（SaccTarget準拠の0.2秒バッチ送信）
        private const float DAMAGESENDINTERVAL = 0.2f;
        private float queuedDamage;
        private float lastDamageSentTime;
        private bool isQueueFlushScheduled;

        // 攻撃者情報
        [System.NonSerialized] public SaccEntity LastAttacker;
        [System.NonSerialized] public VRCPlayerApi LastHitByPlayer;
        [System.NonSerialized] public float LastHitDamage;
        [System.NonSerialized] public byte LastHitWeaponType;

        // ダメージ予測（非オーナー用）
        private float predictedHealth;
        private float predictedLastHitTime = -100f;
        private float predictExplodeTime;

        /// <summary>
        /// SaccEntity拡張の初期化イベント。
        /// 全サブコンポーネントにBossController参照を設定する。
        /// </summary>
        public void SFEXT_L_EntityStart()
        {
            localPlayer = Networking.LocalPlayer;
            if (localPlayer != null)
            {
                isOwner = localPlayer.IsOwner(EntityControl.gameObject);
            }
            else
            {
                isOwner = true;
            }

            Health = MaxHealth;

            // 全DamageRelayにBossController参照を設定
            if (DamageRelays != null)
            {
                foreach (UdonSharpBehaviour relay in DamageRelays)
                {
                    if (relay)
                    {
                        relay.SetProgramVariable("BossController", this);
                        relay.SetProgramVariable("BossEntity", EntityControl);
                    }
                }
            }

            // 全BossPartLinkにBossController参照を設定
            if (BossPartLinks != null)
            {
                foreach (UdonSharpBehaviour link in BossPartLinks)
                {
                    if (link)
                    {
                        link.SetProgramVariable("BossController", this);
                    }
                }
            }

            if (BossAnimator)
            {
                BossAnimator.SetFloat("healthpc", 1f);
                BossAnimator.SetBool("dead", false);
            }

            initialized = true;
        }

        /// <summary>
        /// オーナーシップ取得時イベント。
        /// </summary>
        public void SFEXT_O_TakeOwnership()
        {
            isOwner = true;
        }

        /// <summary>
        /// オーナーシップ喪失時イベント。
        /// </summary>
        public void SFEXT_O_LoseOwnership()
        {
            isOwner = false;
        }

        /// <summary>
        /// 弱点/パーツ破壊からのダメージ受信（ローカル呼び出し）。
        /// BossDamageRelayまたはBossPartLinkから呼ばれる。
        /// ダメージ予測 → SendBossDamageEvent(Self) → QueueDamage(Others)
        /// </summary>
        public void ReceiveDamage(float damage)
        {
            if (dead || EntityControl._dead || EntityControl.invincible) return;
            if (damage <= 0f) return;

            LastHitDamage = damage;
            DamagePrediction();
            // ローカルでダメージ処理
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.Self, nameof(SendBossDamageEvent), damage);
            // 他クライアントへバッチ送信
            QueueDamage(damage);
        }

        /// <summary>
        /// ダメージ予測（非オーナー用）。ローカルで爆発を先行再生する。
        /// </summary>
        private void DamagePrediction()
        {
            if (localPlayer == null) return;
            if (localPlayer.IsOwner(EntityControl.gameObject)) return;
            if (Time.time - predictedLastHitTime > 2f)
            {
                predictedLastHitTime = Time.time;
                predictedHealth = Mathf.Min(Health - LastHitDamage, MaxHealth);
            }
            else
            {
                predictedLastHitTime = Time.time;
                predictedHealth = Mathf.Min(predictedHealth - LastHitDamage, MaxHealth);
            }
            if (!dead && predictedHealth <= 0f)
            {
                PredictExplode();
            }
        }

        /// <summary>
        /// 爆発予測。ローカルで先行してExplodeBossを呼び出す。
        /// </summary>
        private void PredictExplode()
        {
            predictExplodeTime = Time.time;
            ExplodeBoss();
            SendCustomEventDelayedSeconds(nameof(ConfirmExploded), 1.01f);
        }

        /// <summary>
        /// 爆発予測の確認。ネットワークから死亡確定しなければ復元する。
        /// </summary>
        public void ConfirmExploded()
        {
            if (Health <= 0f) return;
            // ネットワーク側で実際にはまだ生きている→状態復元
            OnDeserialization();
        }

        /// <summary>
        /// ダメージキューイング。0.2秒間隔でバッチ送信する（SaccTarget準拠）。
        /// </summary>
        private void QueueDamage(float dmg)
        {
            queuedDamage += dmg;
            if (Time.time - lastDamageSentTime > DAMAGESENDINTERVAL)
            {
                SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.Others, nameof(SendBossDamageEvent), queuedDamage);
                lastDamageSentTime = Time.time;
                queuedDamage = 0;
            }
            else
            {
                if (!isQueueFlushScheduled)
                {
                    isQueueFlushScheduled = true;
                    SendCustomEventDelayedSeconds(nameof(SendQueuedBossDamage), DAMAGESENDINTERVAL);
                }
            }
        }

        /// <summary>
        /// キューに溜まったダメージを送信する。
        /// </summary>
        public void SendQueuedBossDamage()
        {
            if (Time.time - lastDamageSentTime > DAMAGESENDINTERVAL)
            {
                isQueueFlushScheduled = false;
                if (queuedDamage > 0)
                {
                    SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.Others, nameof(SendBossDamageEvent), queuedDamage);
                    lastDamageSentTime = Time.time;
                    queuedDamage = 0;
                }
            }
            else
            {
                SendCustomEventDelayedSeconds(nameof(SendQueuedBossDamage), DAMAGESENDINTERVAL);
            }
        }

        /// <summary>
        /// ネットワーク経由でダメージを受信する。オーナーのみHP減算を行う。
        /// </summary>
        [NetworkCallable]
        public void SendBossDamageEvent(float dmg)
        {
            if (dead || EntityControl._dead || EntityControl.invincible) return;

            LastHitByPlayer = NetworkCalling.CallingPlayer;
            if (Utilities.IsValid(LastHitByPlayer) && LastHitByPlayer != localPlayer)
            {
                GameObject attackersVehicle = GameObject.Find(LastHitByPlayer.GetPlayerTag("SF_VehicleName"));
                if (attackersVehicle)
                    LastAttacker = attackersVehicle.GetComponent<SaccEntity>();
            }
            LastHitDamage = dmg;

            // オーナーのみHP減算
            if (localPlayer != null && localPlayer.IsOwner(EntityControl.gameObject))
            {
                Health = Mathf.Min(Health - Mathf.Max(dmg, 0f), MaxHealth);

                if (Health <= 0f)
                {
                    Health = 0f;
                    // キルイベント送信
                    if (SendKillEvents && Utilities.IsValid(LastHitByPlayer) && !dead)
                    {
                        int killerID = LastHitByPlayer.playerId;
                        if (killerID > -1)
                        {
                            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(BossKillEvent), killerID);
                        }
                    }
                    RequestSerialization();
                    // 全クライアントに爆発を通知
                    NetworkExplodeBoss();
                }
                else
                {
                    RequestSerialization();
                    OnDeserialization();
                }
            }

            EntityControl.SendEventToExtensions("SFEXT_L_WakeUp");
            EntityControl.SendEventToExtensions("SFEXT_G_BulletHit");
        }

        /// <summary>
        /// ボスキルイベント。キルフィードに通知する。
        /// </summary>
        [NetworkCallable]
        public void BossKillEvent(int killerID)
        {
            if (killerID > -1 && KillFeed)
            {
                VRCPlayerApi KillerAPI = VRCPlayerApi.GetPlayerById(killerID);
                if (Utilities.IsValid(KillerAPI))
                {
                    LastHitByPlayer = KillerAPI;
                    GameObject attackersVehicle = GameObject.Find(LastHitByPlayer.GetPlayerTag("SF_VehicleName"));
                    if (attackersVehicle)
                    {
                        LastAttacker = attackersVehicle.GetComponent<SaccEntity>();
                    }

                    if (localPlayer != null && killerID == localPlayer.playerId)
                    {
                        if (BossKilledMessages == null || BossKilledMessages.Length == 0) return;
                        KillFeed.SetProgramVariable("useCustomKillMessage", true);
                        KillFeed.SetProgramVariable("KilledPlayerID", -2);
                        int MsgIndex = (byte)Random.Range(0, BossKilledMessages.Length);
                        string killmessage = BossKilledMessages[MsgIndex];
                        KillFeed.SetProgramVariable("MyKillMsg", killmessage);
                        KillFeed.SendCustomEvent("sendKillMessage");
                        KillFeed.SetProgramVariable("useCustomKillMessage", false);
                    }
                }
            }
        }

        /// <summary>
        /// 全クライアントにボス爆発を通知する。
        /// </summary>
        public void NetworkExplodeBoss()
        {
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(ExplodeBoss));
        }

        /// <summary>
        /// ボスを爆発させる。全パーツ/タレットを破壊し、リスポーンを予約する。
        /// 全クライアントで実行される。
        /// </summary>
        public void ExplodeBoss()
        {
            if (dead) return;
            dead = true;
            EntityControl.dead = true;

            // 全パーツを強制破壊
            if (BossParts != null)
            {
                foreach (UdonSharpBehaviour part in BossParts)
                {
                    if (part)
                    {
                        // SaccTargetのオーナーのみExplode実行
                        if (localPlayer != null && localPlayer.IsOwner(part.gameObject))
                        {
                            part.SendCustomEvent("Explode");
                        }
                    }
                }
            }

            // 全タレットを無効化
            if (TurretEntities != null)
            {
                foreach (SaccEntity turret in TurretEntities)
                {
                    if (turret && !turret._dead)
                    {
                        turret.dead = true;
                    }
                }
            }

            // Animator更新
            if (BossAnimator)
            {
                BossAnimator.SetBool("dead", true);
                BossAnimator.SetFloat("healthpc", 0f);
                BossAnimator.SetTrigger("explode");
            }

            // 演出GameObjectの切替
            if (EnableOnDeath != null)
            {
                foreach (GameObject obj in EnableOnDeath)
                {
                    if (obj) obj.SetActive(true);
                }
            }
            if (DisableOnDeath != null)
            {
                foreach (GameObject obj in DisableOnDeath)
                {
                    if (obj) obj.SetActive(false);
                }
            }

            EntityControl.SendEventToExtensions("SFEXT_G_Dead");

            // リスポーン予約（オーナーのみ）
            if (RespawnDelay > 0f && isOwner)
            {
                SendCustomEventDelayedSeconds(nameof(RespawnBoss), RespawnDelay);
            }
        }

        /// <summary>
        /// ボスをリスポーンする。HP全回復、全パーツ/タレット復活。
        /// オーナーが実行し、全クライアントへ通知する。
        /// </summary>
        public void RespawnBoss()
        {
            if (!isOwner) return;
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(RespawnBossGlobal));
        }

        /// <summary>
        /// ボスリスポーンの全クライアント処理。
        /// </summary>
        public void RespawnBossGlobal()
        {
            dead = false;
            EntityControl.dead = false;

            if (isOwner)
            {
                Health = MaxHealth;
                RequestSerialization();
            }

            // 全パーツをリスポーン
            if (BossParts != null)
            {
                foreach (UdonSharpBehaviour part in BossParts)
                {
                    if (part)
                    {
                        part.SendCustomEvent("RespawnTarget");
                    }
                }
            }

            // 全BossPartLinkにリスポーン通知
            if (BossPartLinks != null)
            {
                foreach (UdonSharpBehaviour link in BossPartLinks)
                {
                    if (link)
                    {
                        link.SendCustomEvent("OnBossRespawn");
                    }
                }
            }

            // Animator更新
            if (BossAnimator)
            {
                BossAnimator.SetBool("dead", false);
                BossAnimator.SetFloat("healthpc", 1f);
                BossAnimator.SetTrigger("reappear");
            }

            // 演出GameObjectの復元
            if (EnableOnDeath != null)
            {
                foreach (GameObject obj in EnableOnDeath)
                {
                    if (obj) obj.SetActive(false);
                }
            }
            if (DisableOnDeath != null)
            {
                foreach (GameObject obj in DisableOnDeath)
                {
                    if (obj) obj.SetActive(true);
                }
            }

            EntityControl.SendEventToExtensions("SFEXT_G_NotDead");

            // 無敵時間設定
            if (InvincibleAfterSpawn > 0f)
            {
                EntityControl.invincible = true;
                SendCustomEventDelayedSeconds(nameof(RemoveInvincibility), InvincibleAfterSpawn);
            }
        }

        /// <summary>
        /// 無敵時間終了。
        /// </summary>
        public void RemoveInvincibility()
        {
            EntityControl.invincible = false;
        }

        /// <summary>
        /// デシリアライズ時の処理。レイトジョイナー対応＋Animator更新。
        /// </summary>
        public override void OnDeserialization()
        {
            // 爆発予測中はスキップ
            if (Time.time - predictExplodeTime < 1f) return;

            if (!initialized) return;

            bool shouldBeDead = Health <= 0f;

            if (BossAnimator)
            {
                BossAnimator.SetFloat("healthpc", Health / MaxHealth);
                BossAnimator.SetBool("dead", shouldBeDead);
                if (Health < MaxHealth && !shouldBeDead)
                {
                    BossAnimator.SetTrigger("hit");
                }
            }

            // レイトジョイナー: 死亡状態の反映
            if (shouldBeDead && !dead)
            {
                ExplodeBoss();
            }
            else if (!shouldBeDead && dead)
            {
                // ネットワーク側で復活済み → ローカルでも復活
                RespawnBossGlobal();
            }
        }

        /// <summary>
        /// オーナーシップ移譲時の処理。
        /// </summary>
        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            if (player == null) return;
            if (player.isLocal)
            {
                isOwner = true;
                if (dead)
                {
                    RequestSerialization();
                }
            }
            else
            {
                isOwner = false;
            }
        }
    }
}

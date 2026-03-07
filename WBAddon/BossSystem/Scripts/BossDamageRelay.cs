
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    /// <summary>
    /// ボスの弱点GameObjectに配置するダメージリレー。
    /// パーティクル被弾を検出し、BossControllerへダメージを転送する。
    ///
    /// 【重要】このGameObjectにはキネマティックRigidbodyが必須。
    /// Unityの仕様で、子コライダーのOnParticleCollisionは親Rigidbodyに発火するため、
    /// 弱点に独自のキネマティックRigidbodyを付けることで、親SaccEntityではなく弱点自体で検出される。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BossDamageRelay : UdonSharpBehaviour
    {
        [Header("ダメージ設定")]
        [Tooltip("装甲値。ダメージをこの値で割る。0の場合は0.000001に補正")]
        public float ArmorStrength = 1f;
        [Tooltip("この値未満のダメージは無効化する")]
        public float NoDamageBelow = 0f;
        [Tooltip("ダメージ倍率。弱点に設定して倍率を上げる等")]
        public float DamageMultiplier = 1f;

        // BossController.SFEXT_L_EntityStart()で自動設定される
        [System.NonSerialized] public BossController BossController;
        // ボスのSaccEntity（攻撃者特定用）
        [System.NonSerialized] public SaccEntity BossEntity;

        private VRCPlayerApi localPlayer;

        private void Start()
        {
            localPlayer = Networking.LocalPlayer;
            if (ArmorStrength == 0f) ArmorStrength = 0.000001f;
        }

        /// <summary>
        /// パーティクル衝突時の処理。d:/t:子オブジェクトをパースし、
        /// 装甲/倍率を適用してBossControllerにダメージを転送する。
        /// SaccTarget.OnParticleCollision準拠のパターン。
        /// </summary>
        void OnParticleCollision(GameObject other)
        {
            if (!other) return;
            if (!BossController) return;
            if (BossEntity && (BossEntity._dead || BossEntity.invincible)) return;

            byte weaponType = 1; // デフォルト武器タイプ
            float damage = 10f / ArmorStrength; // デフォルトダメージ

            // 子オブジェクトからダメージ値と武器タイプをパース
            foreach (Transform child in other.transform)
            {
                string pname = child.name;
                if (pname.StartsWith("d:"))
                {
                    if (float.TryParse(pname.Substring(2), out float dmg))
                    {
                        damage = Mathf.Max(dmg, 0f);
                    }
                }
                else if (pname.StartsWith("t:"))
                {
                    if (byte.TryParse(pname.Substring(2), out byte wt))
                    {
                        weaponType = wt;
                    }
                }
            }

            // 最小ダメージ閾値チェック（SaccTarget準拠: d:未指定時は10f/ArmorStrengthがデフォルト値）
            if (damage > 0 && damage < NoDamageBelow) return;

            // ダメージ倍率適用（弱点ごとの追加倍率）
            damage *= DamageMultiplier;

            // 攻撃者の特定（SaccEntity.WeaponDamageVehicle準拠）
            FindAttacker(other);

            // BossControllerにダメージ転送
            BossController.LastAttacker = BossEntity != null ? BossEntity.LastAttacker : null;
            BossController.LastHitWeaponType = weaponType;
            BossController.ReceiveDamage(damage);

            // 攻撃者にダメージフィードバック
            if (BossController.LastAttacker && BossController.LastAttacker != BossEntity)
            {
                BossController.LastAttacker.SendEventToExtensions("SFEXT_L_DamageFeedback");
            }
        }

        /// <summary>
        /// 攻撃者のSaccEntityをヒエラルキーから検索する。
        /// SaccEntity.WeaponDamageVehicle準拠のパターン。
        /// </summary>
        private void FindAttacker(GameObject damagingObject)
        {
            if (!BossEntity) return;

            SaccEntity EnemyEntityControl = null;
            if (damagingObject)
            {
                // ヒエラルキーを辿ってSaccEntityを直接検索
                GameObject EnemyObjs = damagingObject;
                EnemyEntityControl = damagingObject.GetComponent<SaccEntity>();
                while (!EnemyEntityControl && EnemyObjs.transform.parent)
                {
                    EnemyObjs = EnemyObjs.transform.parent.gameObject;
                    EnemyEntityControl = EnemyObjs.GetComponent<SaccEntity>();
                }
                // 見つからない場合、UdonBehaviourのEntityControl変数を検索（ミサイル等）
                if (!EnemyEntityControl)
                {
                    EnemyObjs = damagingObject;
                    UdonBehaviour EnemyUdonBehaviour = (UdonBehaviour)EnemyObjs.GetComponent(typeof(UdonBehaviour));
                    while (!EnemyUdonBehaviour && EnemyObjs.transform.parent)
                    {
                        EnemyObjs = EnemyObjs.transform.parent.gameObject;
                        EnemyUdonBehaviour = (UdonBehaviour)EnemyObjs.GetComponent(typeof(UdonBehaviour));
                    }
                    if (EnemyUdonBehaviour)
                    {
                        EnemyEntityControl = (SaccEntity)EnemyUdonBehaviour.GetProgramVariable("EntityControl");
                    }
                }
            }
            BossEntity.LastAttacker = EnemyEntityControl;
        }
    }
}

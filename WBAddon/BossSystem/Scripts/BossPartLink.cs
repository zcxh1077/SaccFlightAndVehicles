
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    /// <summary>
    /// ボスパーツとBossControllerを連動させるリンクスクリプト。
    /// SaccTarget.ExplodeOther[]に登録して使用する。
    /// パーツ破壊時にBossControllerへ最大HP分のダメージを送信し、子タレットを無効化する。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BossPartLink : UdonSharpBehaviour
    {
        [Header("パーツ設定")]
        [Tooltip("このパーツのSaccTarget。FullHealthを取得するために使用")]
        public UdonSharpBehaviour PartTarget;
        [Tooltip("パーツ破壊時にボスに与えるダメージ。0の場合はPartTarget.FullHealthを使用")]
        public float DamageOnDestruction = 0f;
        [Tooltip("PartTarget.FullHealthの取得に失敗した場合のフォールバックダメージ")]
        public float FallbackDamage = 1000f;

        [Header("子タレット連動")]
        [Tooltip("このパーツ破壊時に無効化するタレットのSaccEntity")]
        public SaccEntity[] ChildTurrets;

        [Header("演出")]
        [Tooltip("パーツ破壊時に有効化するGameObject")]
        public GameObject[] EnableOnDestroy;
        [Tooltip("パーツ破壊時に無効化するGameObject")]
        public GameObject[] DisableOnDestroy;

        // BossController.SFEXT_L_EntityStart()で自動設定される
        [System.NonSerialized] public BossController BossController;

        // パーツ破壊済みフラグ（二重処理防止）
        private bool partDestroyed;
        private VRCPlayerApi localPlayer;

        private void Start()
        {
            localPlayer = Networking.LocalPlayer;
        }

        /// <summary>
        /// SaccTarget.ExplodeOtherから呼ばれる。パーツ破壊時の処理。
        /// SaccTargetのオーナーのみがBossController.ReceiveDamageを実行する
        /// （全クライアントで呼ばれるため重複防止）。
        /// </summary>
        public void Explode()
        {
            if (partDestroyed) return;
            partDestroyed = true;

            // SaccTargetオーナーのみBossControllerにダメージ送信
            if (BossController && PartTarget != null)
            {
                if (localPlayer != null && localPlayer.IsOwner(PartTarget.gameObject))
                {
                    float damage = DamageOnDestruction;
                    if (damage <= 0f)
                    {
                        // PartTarget(SaccTarget)のFullHealthを取得
                        object fullHealthObj = PartTarget.GetProgramVariable("FullHealth");
                        if (fullHealthObj != null)
                        {
                            damage = (float)fullHealthObj;
                        }
                        else
                        {
                            damage = FallbackDamage;
                        }
                    }
                    BossController.ReceiveDamage(damage);
                }
            }

            // 子タレットを無効化（全クライアントで実行）
            if (ChildTurrets != null)
            {
                foreach (SaccEntity turret in ChildTurrets)
                {
                    if (turret && !turret._dead)
                    {
                        turret.dead = true;
                    }
                }
            }

            // 演出GameObjectの切替（全クライアントで実行）
            if (EnableOnDestroy != null)
            {
                foreach (GameObject obj in EnableOnDestroy)
                {
                    if (obj) obj.SetActive(true);
                }
            }
            if (DisableOnDestroy != null)
            {
                foreach (GameObject obj in DisableOnDestroy)
                {
                    if (obj) obj.SetActive(false);
                }
            }
        }

        /// <summary>
        /// BossControllerからボスリスポーン時に呼ばれる。
        /// パーツ状態をリセットし、子タレットを復活させる。
        /// </summary>
        public void OnBossRespawn()
        {
            partDestroyed = false;

            // 子タレットを復活（全クライアントで実行）
            if (ChildTurrets != null)
            {
                foreach (SaccEntity turret in ChildTurrets)
                {
                    if (turret)
                    {
                        turret.dead = false;
                        // タレットのReAppearイベントを送信
                        turret.SendEventToExtensions("SFEXT_G_ReAppear");
                    }
                }
            }

            // 演出GameObjectの復元
            if (EnableOnDestroy != null)
            {
                foreach (GameObject obj in EnableOnDestroy)
                {
                    if (obj) obj.SetActive(false);
                }
            }
            if (DisableOnDestroy != null)
            {
                foreach (GameObject obj in DisableOnDestroy)
                {
                    if (obj) obj.SetActive(true);
                }
            }
        }
    }
}

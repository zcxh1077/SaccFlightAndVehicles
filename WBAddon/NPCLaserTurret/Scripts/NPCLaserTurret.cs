
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class NPCLaserTurret : UdonSharpBehaviour
    {
        [Header("参照")]
        public SaccEntity EntityControl;
        [Tooltip("タレットの照準部分（回転するオブジェクト）")]
        public Transform Rotator;
        [Tooltip("発射点（砲口）のTransform。Rotatorとのパララックスを補正する。未設定時はRotatorを使用")]
        public Transform MuzzlePoint;
        [Tooltip("レーザー発射アニメーションを制御するAnimator")]
        public Animator TurretAnimator;
        [Tooltip("レーザー発射時に再生するAudioSource")]
        public AudioSource FiringSound;

        [Header("ターゲティング")]
        [Tooltip("ターゲットを捕捉できる最大距離")]
        public float MaxTargetDistance = 3000f;
        [Tooltip("ターゲット捕捉の半角コーン角度")]
        public float LockAngle = 30f;
        [Tooltip("ターゲット検出レイキャストで使用するレイヤー")]
        [SerializeField] private int[] TargetLayers = { 17, 31 };

        [Header("回転")]
        [Tooltip("ターゲットなし時のアイドル仰角（負値が上向き）")]
        [SerializeField] private float IdleAimAngle = -45f;
        [Tooltip("回転速度の倍率")]
        public float TurnSpeedMulti = 6f;
        [Tooltip("フレーム毎の回転減衰")]
        public float TurnFriction = 4f;
        [Tooltip("水平線より上方向への最大仰角")]
        public float UpAngleMax = 89f;
        [Tooltip("水平線より下方向への最大俯角")]
        public float DownAngleMax = 35f;
        [Tooltip("AI照準用のP制御ゲイン")]
        public float AI_TurnStrength = 1f;

        [Header("方位角制限")]
        [Tooltip("方位角制限を有効にする。有効時はターゲットなし時に左右スキャン動作を行う")]
        public bool AzimuthLimitEnabled = false;
        [Tooltip("初期方向から左（負のヨー）への最大回転角度")]
        public float AzimuthLeft = 90f;
        [Tooltip("初期方向から右（正のヨー）への最大回転角度")]
        public float AzimuthRight = 90f;

        [Header("射撃")]
        [Tooltip("偏差射撃予測用の弾速 (m/s)。ターゲットがローカルクライアントで所有されている場合に使用")]
        public float BulletSpeed = 3000f;
        [Tooltip("非オーナーターゲットに対する偏差射撃予測用の弾速 (m/s)。ネットワーク遅延による位置ずれを補正するために調整する。0の場合はBulletSpeedを使用")]
        public float BulletSpeedNonOwner = 0f;
        [Tooltip("バースト間の休止時間の最小/最大値 (秒)")]
        public Vector2 BurstPauseLength = new Vector2(2f, 5f);
        [Tooltip("射撃時に使用するAnimatorトリガー名")]
        public string AnimTriggerFire = "fire";
        [Tooltip("射撃中状態を検出するAnimatorステートタグ")]
        public string AnimBusyTag = "busy";
        [Tooltip("この角度以下で射撃を開始する（ターゲットリード位置との角度）")]
        public float FireAngle = 10f;

        [Header("体力")]
        [Tooltip("タレットの体力")]
        public float Health = 100f;
        [Tooltip("破壊後のリスポーン遅延 (秒)")]
        public float RespawnDelay = 20f;
        [Tooltip("リスポーン後の無敵時間 (秒)")]
        public float InvincibleAfterSpawn = 1f;
        [Tooltip("HP自動回復の間隔 (秒)")]
        public float HPRepairDelay = 5f;
        [Tooltip("各回復間隔で回復するHP量")]
        public float HPRepairAmount = 5f;
        [Tooltip("ネットワーク同期を待たずにローカルで爆発を予測する")]
        public bool PredictExplosion = true;
        [Tooltip("このタレットを撃破した際にキルイベントを送信する")]
        public bool SendKillEvents;

        // 同期変数
        [UdonSynced] private int targetIndex = -1;
        [UdonSynced] private byte fireCount;

        // キャッシュされた参照
        private GameObject[] aamTargets;
        private int numAAMTargets;
        private VRCPlayerApi localPlayer;
        private bool inEditor = true;
        private bool isOwner;
        private float fullHealth;
        private float fullHealthDivider;
        private Quaternion startRot;
        private float bulletSpeedDivider;
        private float bulletSpeedNonOwnerDivider;
        private int raycastLayerMask;
        private int targetLayerBitmask;

        // ターゲティング状態
        private int aamTargetChecker;
        private bool hasTarget;
        private SaccAirVehicle currentTargetSAVControl;
        private float targetObscuredDelay = 999f;

        // 偏差射撃予測
        private Vector3 targetDirOld;
        private float targetSpeedLerper;
        private Vector3 cachedLeadPos;

        // 回転
        private float rotationSpeedX;
        private float rotationSpeedY;
        // 回転追跡（Euler読み戻しの代替）
        private float currentPitch;
        private float currentYaw;
        private float startYaw;
        private float idleScanDir = 1f;

        // 射撃
        private byte localFireCount;
        private bool initialSyncDone;
        private bool isBusy;
        private float pauseTimer;

        private float currentPauseLength;

        // 体力
        private float hpRepairTimer;
        private float lastHitTime = -100f;
        private float predictedHealth;

        /// <summary>
        /// SaccEntityから呼ばれる初期化イベント。
        /// オーナー判定、HP初期化、ターゲットリスト取得、レイキャストマスク構築を行う。
        /// </summary>
        public void SFEXT_L_EntityStart()
        {
            localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
            {
                inEditor = true;
                isOwner = true;
            }
            else
            {
                inEditor = false;
                isOwner = EntityControl.IsOwner;
            }

            fullHealth = Health;
            fullHealthDivider = 1f / (Health > 0 ? Health : 10000000f);
            startRot = Rotator.localRotation;
            Vector3 se = startRot.eulerAngles;
            currentPitch = se.x > 180f ? se.x - 360f : se.x;
            currentYaw = se.y;
            startYaw = currentYaw;
            bulletSpeedDivider = 1f / BulletSpeed;
            if (BulletSpeedNonOwner <= 0f) { BulletSpeedNonOwner = BulletSpeed; }
            bulletSpeedNonOwnerDivider = 1f / BulletSpeedNonOwner;

            aamTargets = EntityControl.AAMTargets;
            if (aamTargets == null) { aamTargets = new GameObject[0]; }
            numAAMTargets = aamTargets.Length;

            // レイキャストレイヤーマスクを構築: Default(0), Water(4), Environment(11), OutsideVehicleLayer, OnboardVehicleLayer
            // NPCタレットは搭乗中の車両を検出するためOnboardVehicleLayerが常に必要
            raycastLayerMask = (1 << 0) | (1 << 4) | (1 << 11)
                             | (1 << EntityControl.OutsideVehicleLayer)
                             | (1 << EntityControl.OnboardVehicleLayer);
            // TargetLayersの全レイヤーをレイキャストマスクに追加し、視線チェックで検出可能にする
            for (int i = 0; i < TargetLayers.Length; i++)
            {
                raycastLayerMask |= (1 << TargetLayers[i]);
                targetLayerBitmask |= (1 << TargetLayers[i]);
            }

            if (TurretAnimator == null)
            {
                TurretAnimator = EntityControl.GetComponent<Animator>();
            }
            if (TurretAnimator)
            {
                TurretAnimator.SetFloat("health", 1f);
            }

            currentPauseLength = Random.Range(BurstPauseLength.x, BurstPauseLength.y);
            pauseTimer = 0f;

            gameObject.SetActive(true);
        }

        /// <summary>
        /// 毎フレーム後半に実行。照準回転、射撃判定、HP回復を処理する。
        /// </summary>
        void LateUpdate()
        {
            if (EntityControl._dead) return;

            // Animatorのビジー状態を確認
            if (TurretAnimator)
            {
                AnimatorStateInfo stateInfo = TurretAnimator.GetCurrentAnimatorStateInfo(0);
                isBusy = stateInfo.IsTag(AnimBusyTag);
            }

            // 回転処理: 全クライアントがローカルで計算、ビジー時はスキップ
            if (!isBusy)
            {
                AimAtTarget();
            }
            else if (hasTarget && targetIndex >= 0 && targetIndex < numAAMTargets)
            {
                // ビジー状態中もtargetDirOldを更新し続ける
                // 照準再開時に速度推定がスパイクしないようにする
                UpdateTargetDirOld();
            }

            // オーナーのみ: 射撃判定とHP回復
            if (isOwner)
            {
                if (!isBusy && hasTarget)
                {
                    Vector3 aimOrigin = GetAimOrigin();
                    Vector3 aimForward = MuzzlePoint ? MuzzlePoint.forward : Rotator.forward;
                    float aimAngle = Vector3.Angle(aimForward, cachedLeadPos - aimOrigin);
                    if (aimAngle < FireAngle)
                    {
                        TryFire();
                    }
                    else
                    {
                        // 照準が不十分なため、休止時間を蓄積
                        pauseTimer += Time.deltaTime;
                    }
                }

                HPReplenishment();
            }
        }

        /// <summary>
        /// FixedUpdate: オーナーのみターゲット検索を実行する。
        /// </summary>
        private void FixedUpdate()
        {
            if (isOwner && !EntityControl._dead)
            {
                FindTargets();
            }
        }

        // --- 偏差射撃予測 ---

        /// <summary>
        /// MuzzlePoint が設定されていればその位置、なければ Rotator.position を返す。
        /// </summary>
        private Vector3 GetAimOrigin()
        {
            return MuzzlePoint ? MuzzlePoint.position : Rotator.position;
        }

        /// <summary>
        /// 現在のターゲット位置を取得する。
        /// SAVControlがある場合はCenterOfMassを使用する。
        /// </summary>
        private Vector3 GetCurrentTargetPos()
        {
            if (currentTargetSAVControl)
            {
                return currentTargetSAVControl.CenterOfMass.position;
            }
            return aamTargets[targetIndex].transform.position;
        }

        /// <summary>
        /// ターゲット方向の前フレーム値を更新する。
        /// ビジー状態中に呼ばれ、照準再開時の速度推定スパイクを防止する。
        /// </summary>
        private void UpdateTargetDirOld()
        {
            Vector3 targetPos = GetCurrentTargetPos();
            targetDirOld = targetPos - GetAimOrigin();
            targetSpeedLerper = 0f;
        }

        /// <summary>
        /// ターゲットの移動速度を考慮した偏差射撃位置（リード位置）を計算する。
        /// フレーム間の位置差分からターゲット速度を推定し、弾の飛翔時間を考慮して予測位置を算出する。
        /// </summary>
        private Vector3 CalculateLeadPosition()
        {
            Vector3 targetPos = GetCurrentTargetPos();
            Vector3 aimOrigin = GetAimOrigin();

            Vector3 targetDir = targetPos - aimOrigin;
            Vector3 relativeTargetVel = targetDir - targetDirOld;
            targetDirOld = targetDir;

            float instantSpeed = relativeTargetVel.magnitude / Time.deltaTime;
            targetSpeedLerper = Mathf.Lerp(targetSpeedLerper, instantSpeed, 12f * Time.deltaTime);

            Vector3 targetVel = relativeTargetVel.normalized * targetSpeedLerper;
            // ターゲットがローカルで所有されているかに応じて弾速を切り替え
            // 非オーナーターゲットはネットワーク遅延で位置が遅れるため、別の弾速で補正する
            float effectiveBulletSpeed = BulletSpeed;
            if (currentTargetSAVControl && !currentTargetSAVControl.EntityControl.IsOwner)
            {
                effectiveBulletSpeed = BulletSpeedNonOwner;
            }
            float interceptTime = Vintercept(aimOrigin, effectiveBulletSpeed, targetPos, targetVel);
            Vector3 predictedPos = targetPos + targetVel * interceptTime;

            return predictedPos;
        }

        /// <summary>
        /// 弾とターゲットの会合時間を二次方程式で求める。
        /// 弾速、ターゲット位置、ターゲット速度から最短の正の会合時間を返す。
        /// </summary>
        /// <param name="fireorg">発射位置</param>
        /// <param name="missilespeed">弾速</param>
        /// <param name="tgtorg">ターゲット位置</param>
        /// <param name="tgtvel">ターゲット速度ベクトル</param>
        /// <returns>会合までの推定時間 (秒)</returns>
        private float Vintercept(Vector3 fireorg, float missilespeed, Vector3 tgtorg, Vector3 tgtvel)
        {
            if (missilespeed <= 0)
                return 0f;

            float tgtspd = tgtvel.magnitude;
            Vector3 dir = fireorg - tgtorg;
            float d = dir.magnitude;
            float a = missilespeed * missilespeed - tgtspd * tgtspd;
            float b = 2 * Vector3.Dot(dir, tgtvel);
            float c = -d * d;

            float t = 0;
            if (a == 0)
            {
                if (b == 0)
                    return 0f;
                else
                    t = -c / b;
            }
            else
            {
                float s0 = b * b - 4 * a * c;
                if (s0 <= 0)
                    return 0f;
                float s = Mathf.Sqrt(s0);
                float div = 1.0f / (2f * a);
                float t1 = -(s + b) * div;
                float t2 = (s - b) * div;
                if (t1 <= 0 && t2 <= 0)
                    return 0f;
                t = (t1 > 0 && t2 > 0) ? Mathf.Min(t1, t2) : Mathf.Max(t1, t2);
            }
            return t;
        }

        // --- 回転制御 ---

        /// <summary>
        /// ターゲットに向けて照準を合わせる。
        /// ターゲットがいる場合はリード位置に向けてP制御で照準、いない場合はアイドル旋回する。
        /// </summary>
        private void AimAtTarget()
        {
            float inputX;
            float inputY;

            if (hasTarget && targetIndex >= 0 && targetIndex < numAAMTargets)
            {
                Vector3 leadPos = CalculateLeadPosition();
                cachedLeadPos = leadPos;
                Vector3 gunForward = Rotator.forward;
                Vector3 aimOrigin = GetAimOrigin();

                // P制御によるヨー（水平旋回）
                Vector3 flattenedTargVec = Vector3.ProjectOnPlane(leadPos - aimOrigin, Rotator.up);
                float yAngle = Vector3.SignedAngle(gunForward, flattenedTargVec, Rotator.up);
                inputY = Mathf.Clamp(yAngle * AI_TurnStrength, -1f, 1f);

                // ヨー分を先に回転してからピッチを計算し、オーバーシュートを防ぐ
                gunForward = Quaternion.AngleAxis(yAngle, EntityControl.transform.up) * gunForward;
                Quaternion forwardX = Rotator.rotation * Quaternion.AngleAxis(yAngle, EntityControl.transform.up);
                Vector3 xRight = forwardX * Vector3.right;

                // P制御によるピッチ（仰俯角）
                flattenedTargVec = Vector3.ProjectOnPlane(leadPos - aimOrigin, xRight);
                inputX = Vector3.SignedAngle(gunForward, flattenedTargVec, xRight);
                inputX = Mathf.Clamp(inputX * AI_TurnStrength, -1f, 1f);
            }
            else
            {
                // ターゲットなし: アイドル動作
                inputX = Mathf.Clamp((IdleAimAngle - currentPitch) * AI_TurnStrength, -1f, 1f);
                if (AzimuthLimitEnabled)
                {
                    // 制限範囲内を左右にスキャン（RotateGunで端に達したとき idleScanDir が反転する）
                    inputY = Mathf.Clamp(idleScanDir * AI_TurnStrength, -1f, 1f);
                }
                else
                {
                    inputY = Mathf.Clamp(1f * AI_TurnStrength, -1f, 1f);
                }
            }

            inputX *= TurnSpeedMulti;
            inputY *= TurnSpeedMulti;
            RotateGun(inputX, inputY);
        }

        /// <summary>
        /// タレットの回転を適用する。
        /// 入力に基づいて回転速度を加速し、摩擦で減速させ、仰俯角の制限を適用する。
        /// </summary>
        /// <param name="inputx">ピッチ入力</param>
        /// <param name="inputy">ヨー入力</param>
        private void RotateGun(float inputx, float inputy)
        {
            float deltaTime = Time.deltaTime;
            float frictionFactor = 1 - Mathf.Pow(0.5f, TurnFriction * deltaTime);
            rotationSpeedX = Mathf.Lerp(rotationSpeedX, 0, frictionFactor) + inputx * deltaTime;
            rotationSpeedY = Mathf.Lerp(rotationSpeedY, 0, frictionFactor) + inputy * deltaTime;

            currentPitch += rotationSpeedX * deltaTime;
            if (currentPitch > DownAngleMax || currentPitch < -UpAngleMax) rotationSpeedX = 0f;
            currentPitch = Mathf.Clamp(currentPitch, -UpAngleMax, DownAngleMax);
            currentYaw += rotationSpeedY * deltaTime;
            if (AzimuthLimitEnabled)
            {
                float yawMin = startYaw - AzimuthLeft;
                float yawMax = startYaw + AzimuthRight;
                if (currentYaw >= yawMax)
                {
                    rotationSpeedY = 0f;
                    idleScanDir = -1f;
                }
                else if (currentYaw <= yawMin)
                {
                    rotationSpeedY = 0f;
                    idleScanDir = 1f;
                }
                currentYaw = Mathf.Clamp(currentYaw, yawMin, yawMax);
            }
            Rotator.localRotation = Quaternion.Euler(currentPitch, currentYaw, 0f);
        }

        // --- 射撃 ---

        /// <summary>
        /// 射撃を試みる。休止タイマーが満了していれば発射し、ネットワーク同期を要求する。
        /// </summary>
        private void TryFire()
        {
            pauseTimer += Time.deltaTime;
            if (pauseTimer >= currentPauseLength)
            {
                // 発射
                fireCount++;
                if (TurretAnimator) { TurretAnimator.SetTrigger(AnimTriggerFire); }
                if (FiringSound) { FiringSound.PlayOneShot(FiringSound.clip); }
                RequestSerialization();

                // 次のバーストに向けて休止タイマーをリセット
                pauseTimer = 0f;
                currentPauseLength = Random.Range(BurstPauseLength.x, BurstPauseLength.y);
            }
        }

        // --- ターゲット検索（オーナーのみ） ---

        /// <summary>
        /// ターゲットの検索と検証を行う（オーナーのみ実行）。
        /// 現在のターゲットの有効性を確認し、ラウンドロビンで新しい候補を1フレーム1つずつ評価する。
        /// 視線チェック、距離、角度、タキシング/死亡状態を考慮してターゲットを選定する。
        /// </summary>
        private void FindTargets()
        {
            if (numAAMTargets == 0) return;

            float deltaTime = Time.deltaTime;
            Vector3 rotatorPos = GetAimOrigin();

            // 現在のターゲットの有効性を検証
            if (targetIndex >= 0 && targetIndex < numAAMTargets)
            {
                var currentTarget = aamTargets[targetIndex];
                if (!currentTarget || !currentTarget.activeInHierarchy)
                {
                    ClearTarget();
                }
                else
                {
                    Vector3 currentTargetDir = currentTarget.transform.position - rotatorPos;
                    float currentDist = currentTargetDir.magnitude;

                    // 現在ターゲットの視線チェック（レイキャスト）
                    RaycastHit hitcurrent;
                    bool lineOfSight = Physics.Raycast(rotatorPos, currentTargetDir, out hitcurrent, Mathf.Infinity, raycastLayerMask, QueryTriggerInteraction.Ignore);
                    bool hitIsCurrentTarget = hitcurrent.collider &&
                        (hitcurrent.collider.gameObject == currentTarget ||
                         hitcurrent.collider.transform.IsChildOf(currentTarget.transform));
                    if (!lineOfSight || !hitIsCurrentTarget)
                    {
                        targetObscuredDelay += deltaTime;
                    }
                    else
                    {
                        targetObscuredDelay = 0f;
                    }

                    // 現在ターゲットがまだ有効か確認
                    bool currentValid = targetObscuredDelay < 0.25f
                        && currentDist < MaxTargetDistance
                        && currentTarget.activeInHierarchy
                        && IsWithinAzimuthLimits(currentTarget.transform.position)
                        && (!currentTargetSAVControl || (!currentTargetSAVControl.Taxiing && !currentTargetSAVControl.EntityControl._dead));

                    if (!currentValid)
                    {
                        ClearTarget();
                    }
                    else
                    {
                        hasTarget = true;
                    }
                }
            }

            // 候補ターゲットを1フレームに1つずつ確認（ラウンドロビン）
            var candidateTarget = aamTargets[aamTargetChecker];
            if (candidateTarget && candidateTarget.activeInHierarchy)
            {
                SaccAirVehicle candidateSAV = null;
                Transform candidateParent = candidateTarget.transform.parent;
                if (candidateParent)
                {
                    candidateSAV = candidateParent.GetComponent<SaccAirVehicle>();
                }

                if (!candidateSAV || (!candidateSAV.Taxiing && !candidateSAV.EntityControl._dead))
                {
                    Vector3 candidateDir = candidateTarget.transform.position - rotatorPos;
                    float candidateAngle = Vector3.Angle(Rotator.forward, candidateDir);
                    float candidateDist = candidateDir.magnitude;

                    // 候補の視線チェック（レイキャスト）
                    RaycastHit hitnext;
                    bool lineOfSight = Physics.Raycast(rotatorPos, candidateDir, out hitnext, Mathf.Infinity, raycastLayerMask, QueryTriggerInteraction.Ignore);
                    bool hitIsCandidate = hitnext.collider &&
                        (hitnext.collider.gameObject == candidateTarget ||
                         hitnext.collider.transform.IsChildOf(candidateTarget.transform));

                    float currentTargetAngle = hasTarget ? Vector3.Angle(Rotator.forward, aamTargets[targetIndex].transform.position - rotatorPos) : 999f;

                    if (lineOfSight
                        && hitIsCandidate
                        && candidateAngle < LockAngle
                        && candidateDist < MaxTargetDistance
                        && IsWithinAzimuthLimits(candidateTarget.transform.position)
                        && (candidateAngle < currentTargetAngle || !hasTarget || targetObscuredDelay > 0.25f))
                    {
                        targetIndex = aamTargetChecker;
                        hasTarget = true;
                        currentTargetSAVControl = candidateSAV;
                        targetObscuredDelay = 0f;
                        targetDirOld = candidateTarget.transform.position - rotatorPos;
                        targetSpeedLerper = 0f;
                        RequestSerialization();
                    }
                }
            }

            // ラウンドロビンのインデックスを進める（現在のターゲットはスキップ）
            aamTargetChecker = (aamTargetChecker + 1) % numAAMTargets;
            if (aamTargetChecker == targetIndex)
            {
                aamTargetChecker = (aamTargetChecker + 1) % numAAMTargets;
            }
        }

        /// <summary>
        /// 現在のターゲットをクリアし、関連する状態をリセットする。
        /// </summary>
        private void ClearTarget()
        {
            if (hasTarget)
            {
                hasTarget = false;
                targetIndex = -1;
                currentTargetSAVControl = null;
                targetObscuredDelay = 999f;
                targetSpeedLerper = 0f;
                if (isOwner) { RequestSerialization(); }
            }
        }

        /// <summary>
        /// 指定したワールド座標がタレットの方位角制限内にあるか判定する。
        /// AzimuthLimitEnabled が false の場合は常に true を返す。
        /// 親Transformのローカル空間で中心方向との角度を計算する。
        /// </summary>
        private bool IsWithinAzimuthLimits(Vector3 worldPos)
        {
            if (!AzimuthLimitEnabled) return true;
            Transform parent = Rotator.parent;
            if (!parent) return true;
            // ターゲット方向を親のローカル空間に変換し、ピッチ成分を除去
            Vector3 localToTarget = parent.InverseTransformDirection(worldPos - Rotator.position);
            localToTarget.y = 0f;
            if (localToTarget.sqrMagnitude < 0.001f) return true;
            // 中心方向（startYaw 時の前方）との符号付き角度を求める
            Vector3 centerDir = Quaternion.Euler(0f, startYaw, 0f) * Vector3.forward;
            float yawOffset = Vector3.SignedAngle(centerDir, localToTarget, Vector3.up);
            return yawOffset >= -AzimuthLeft && yawOffset <= AzimuthRight;
        }

        /// <summary>
        /// レイキャストのヒットがターゲットレイヤーに含まれるか判定する。
        /// </summary>
        private bool RayhitIsOnCorrectLayer(RaycastHit target)
        {
            if (!target.collider) return false;
            return (targetLayerBitmask & (1 << target.collider.gameObject.layer)) != 0;
        }



        // --- 体力管理 ---

        /// <summary>
        /// HP自動回復処理。一定間隔でHPRepairを呼び出す（オーナーのみ）。
        /// </summary>
        private void HPReplenishment()
        {
            if (Health >= fullHealth)
            {
                hpRepairTimer = 0;
                return;
            }
            hpRepairTimer += Time.deltaTime;
            if (hpRepairTimer > HPRepairDelay)
            {
                hpRepairTimer = 0f;
                if (inEditor)
                {
                    HPRepair();
                }
                else
                {
                    SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(HPRepair));
                }
            }
        }

        /// <summary>
        /// HPを回復する。全クライアントで実行される。
        /// </summary>
        public void HPRepair()
        {
            Health += HPRepairAmount;
            if (Health >= fullHealth)
            {
                Health = fullHealth;
                hpRepairTimer = float.MinValue;
            }
            else
            {
                hpRepairTimer = 0;
            }
            if (TurretAnimator) { TurretAnimator.SetFloat("health", Health * fullHealthDivider); }
        }

        // --- 爆発 / リスポーン ---

        /// <summary>
        /// ネットワーク経由で全クライアントにExplodeを送信する。
        /// </summary>
        public void NetworkExplode()
        {
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Explode));
        }

        /// <summary>
        /// タレットを爆発させる。HP・回転速度をリセットし、爆発アニメーションを再生。
        /// RespawnDelay後にリスポーンする。
        /// </summary>
        public void Explode()
        {
            if (EntityControl._dead) return;

            EntityControl.dead = true;
            ClearTarget();
            Health = fullHealth;
            rotationSpeedX = 0;
            rotationSpeedY = 0;

            // 全クライアントで追跡フィールドをリセット
            Vector3 se = startRot.eulerAngles;
            currentPitch = se.x > 180f ? se.x - 360f : se.x;
            currentYaw = se.y;

            if (TurretAnimator)
            {
                TurretAnimator.SetFloat("health", 1f);
                TurretAnimator.SetTrigger("explode");
            }
            if (isOwner)
            {
                Rotator.localRotation = startRot;
            }

            SendCustomEventDelayedSeconds(nameof(ReAppear), RespawnDelay);
            SendCustomEventDelayedSeconds(nameof(NotDead), RespawnDelay + InvincibleAfterSpawn);
            EntityControl.SendEventToExtensions("SFEXT_G_Explode");
        }

        /// <summary>
        /// タレットを再表示する。リスポーンアニメーションを再生し、死亡状態を解除する。
        /// </summary>
        public void ReAppear()
        {
            if (TurretAnimator) { TurretAnimator.SetTrigger("reappear"); }
            EntityControl.invincible = true;
            EntityControl.dead = false;
            if (isOwner)
            {
                Health = fullHealth;
            }
            EntityControl.SendEventToExtensions("SFEXT_G_ReAppear");
        }

        /// <summary>
        /// 無敵時間終了後にHPを全回復し、完全に生存状態にする。
        /// </summary>
        public void NotDead()
        {
            Health = fullHealth;
            EntityControl.dead = false;
            EntityControl.invincible = false;
        }

        // --- SaccEntity イベント ---

        /// <summary>
        /// オーナーシップ取得時に呼ばれる。
        /// 回転速度をリセットし、現在のターゲットのSAVControlを再取得する。
        /// </summary>
        public void SFEXT_O_TakeOwnership()
        {
            isOwner = true;
            rotationSpeedX = 0;
            rotationSpeedY = 0;
            // 現在のターゲットのSAVControlを正しく設定
            if (targetIndex >= 0 && targetIndex < numAAMTargets)
            {
                var target = aamTargets[targetIndex];
                if (target && target.transform.parent)
                {
                    currentTargetSAVControl = target.transform.parent.GetComponent<SaccAirVehicle>();
                }
            }
        }

        /// <summary>
        /// オーナーシップ喪失時に呼ばれる。
        /// </summary>
        public void SFEXT_O_LoseOwnership()
        {
            isOwner = false;
        }

        /// <summary>
        /// 被弾イベント（オーナー処理）。
        /// ダメージを適用し、HPが0以下になったら爆発をネットワーク送信する。
        /// </summary>
        public void SFEXT_G_BulletHit()
        {
            if (!isOwner || EntityControl._dead || EntityControl.invincible) return;
            Health = Mathf.Min(Health - EntityControl.LastHitDamage, fullHealth);
            if (TurretAnimator) { TurretAnimator.SetFloat("health", Health * fullHealthDivider); }
            if (Health <= 0f)
            {
                NetworkExplode();
            }
        }

        /// <summary>
        /// 被弾イベント（ローカル予測処理）。
        /// PredictExplosionが有効な場合、ネットワーク同期を待たずにローカルで爆発を予測する。
        /// 2秒以上間隔が空いた場合はpredictedHealthをリセットする。
        /// </summary>
        public void SFEXT_L_BulletHit()
        {
            if (isOwner || EntityControl._dead || EntityControl.invincible) return;
            if (PredictExplosion)
            {
                if (Time.time - lastHitTime > 2f)
                {
                    lastHitTime = Time.time;
                    predictedHealth = Mathf.Min(Health - EntityControl.LastHitDamage, fullHealth);
                    if (predictedHealth < 0f)
                    {
                        Explode();
                    }
                }
                else
                {
                    lastHitTime = Time.time;
                    predictedHealth -= EntityControl.LastHitDamage;
                    if (predictedHealth < 0f)
                    {
                        Explode();
                    }
                }
            }
        }

        // --- ネットワーク同期 ---

        /// <summary>
        /// ネットワークデシリアライズ時に呼ばれる。
        /// 射撃カウントの変化を検出してアニメーション・効果音を再生し、
        /// ターゲットインデックスの変化に応じて追跡状態を更新する。
        /// </summary>
        public override void OnDeserialization()
        {
            // 射撃の同期
            // 初回デシリアライズは fireCount の値に関わらずスキップ（レイトジョイナー対策）
            if (!initialSyncDone)
            {
                initialSyncDone = true;
                localFireCount = fireCount;
            }
            else if (fireCount != localFireCount)
            {
                localFireCount = fireCount;
                if (TurretAnimator) { TurretAnimator.SetTrigger(AnimTriggerFire); }
                if (FiringSound) { FiringSound.PlayOneShot(FiringSound.clip); }
            }

            // ターゲットの同期
            if (targetIndex >= 0 && targetIndex < numAAMTargets)
            {
                hasTarget = true;
                var target = aamTargets[targetIndex];
                if (target && target.transform.parent)
                {
                    currentTargetSAVControl = target.transform.parent.GetComponent<SaccAirVehicle>();
                }
                else
                {
                    currentTargetSAVControl = null;
                }
                // 新しいターゲットに対してリード予測状態をリセット
                targetDirOld = target ? (target.transform.position - GetAimOrigin()) : Vector3.forward;
                targetSpeedLerper = 0f;
            }
            else
            {
                hasTarget = false;
                currentTargetSAVControl = null;
            }
        }
    }
}

using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace SaccFlightAndVehicles
{
    /// <summary>
    /// BossController のカスタムインスペクター。
    /// ボスルート以下のヒエラルキーを走査し、参照フィールドを一括自動設定する。
    ///
    /// 自動設定される項目:
    ///   BossController — BossParts / BossPartLinks / TurretEntities / DamageRelays / BossAnimator
    ///   BossPartLink  — PartTarget / ChildTurrets
    ///   SaccEntity    — ExtensionUdonBehaviours に BossController を追加
    ///   SaccTarget    — ExplodeOther に BossPartLink を追加
    /// </summary>
    [CustomEditor(typeof(BossController))]
    public class BossControllerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.Space(4);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("自動セットアップ", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "ボスルート以下のヒエラルキーを走査し、参照フィールドを一括設定します。\n" +
                    "BossPartLink の PartTarget / ChildTurrets、SaccTarget.ExplodeOther への\n" +
                    "登録、SaccEntity.ExtensionUdonBehaviours への登録も同時に行います。",
                    EditorStyles.wordWrappedMiniLabel);

                if (GUILayout.Button("ヒエラルキーから自動設定", GUILayout.Height(28)))
                {
                    RunAutoSetup();
                }
            }

            EditorGUILayout.Space(4);
            DrawDefaultInspector();
        }

        // ====================================================================
        //  自動セットアップ本体
        // ====================================================================

        private void RunAutoSetup()
        {
            var controller = (BossController)target;

            // --- 1. ボス本体の SaccEntity を探す ---
            SaccEntity bossEntity = FindInSelfOrAncestors<SaccEntity>(controller.transform);
            if (bossEntity == null)
            {
                EditorUtility.DisplayDialog(
                    "自動設定エラー",
                    "BossController の祖先に SaccEntity が見つかりません。\n" +
                    "BossBody（SaccEntity 付き）の子に配置してください。",
                    "OK");
                return;
            }

            // --- 2. 検索ルート決定（BossBody の親 = BossRoot）---
            Transform searchRoot = bossEntity.transform.parent != null
                ? bossEntity.transform.parent
                : bossEntity.transform;

            // --- 3. コンポーネント収集 ---
            var relays = new List<BossDamageRelay>(
                searchRoot.GetComponentsInChildren<BossDamageRelay>(true));
            var parts = new List<SaccTarget>(
                searchRoot.GetComponentsInChildren<SaccTarget>(true));
            var links = new List<BossPartLink>(
                searchRoot.GetComponentsInChildren<BossPartLink>(true));

            // タレット = ボス本体以外の SaccEntity
            var turrets = new List<SaccEntity>();
            foreach (var e in searchRoot.GetComponentsInChildren<SaccEntity>(true))
            {
                if (e != bossEntity)
                    turrets.Add(e);
            }

            // --- 4. BossController フィールド設定 ---
            serializedObject.Update();

            SetArrayProperty("DamageRelays", relays);
            SetArrayProperty("BossParts", parts);
            SetArrayProperty("BossPartLinks", links);
            SetArrayProperty("TurretEntities", turrets);

            // BossAnimator 自動検出（未設定の場合のみ）
            var animProp = serializedObject.FindProperty("BossAnimator");
            if (animProp.objectReferenceValue == null)
            {
                Animator anim = bossEntity.GetComponent<Animator>();
                if (anim == null)
                    anim = searchRoot.GetComponent<Animator>();
                if (anim != null)
                    animProp.objectReferenceValue = anim;
            }

            serializedObject.ApplyModifiedProperties();

            // --- 5. SaccEntity.ExtensionUdonBehaviours に BossController を登録 ---
            EnsureInArray(bossEntity, "ExtensionUdonBehaviours", controller);

            // --- 6. 各 BossPartLink の自動設定 ---
            int linkConfigured = 0;
            foreach (var link in links)
            {
                if (SetupPartLink(link, bossEntity))
                    linkConfigured++;
            }

            // --- 7. バリデーション ---
            var warnings = Validate(bossEntity, parts, relays);

            // --- 8. 結果表示 ---
            string msg =
                "自動設定完了\n\n" +
                $"  DamageRelays:   {relays.Count}\n" +
                $"  BossParts:      {parts.Count}\n" +
                $"  BossPartLinks:  {links.Count}（うち {linkConfigured} 件を設定）\n" +
                $"  TurretEntities: {turrets.Count}";

            if (warnings.Count > 0)
                msg += "\n\n--- 警告 ---\n" + string.Join("\n", warnings);

            Debug.Log("[BossController 自動設定] " + msg.Replace("\n\n", " | ").Replace("\n", ", "));
            EditorUtility.DisplayDialog("BossController 自動設定", msg, "OK");
        }

        // ====================================================================
        //  BossPartLink 個別設定
        // ====================================================================

        /// <summary>
        /// BossPartLink の PartTarget / ChildTurrets を設定し、
        /// 対応する SaccTarget.ExplodeOther に登録する。
        /// </summary>
        private bool SetupPartLink(BossPartLink link, SaccEntity bossEntity)
        {
            // 祖先の SaccTarget を PartTarget として設定
            SaccTarget partTarget = FindInSelfOrAncestors<SaccTarget>(link.transform);
            if (partTarget == null) return false;

            // パーツ以下の SaccEntity（ボス本体を除く）を ChildTurrets として設定
            var childTurrets = new List<SaccEntity>();
            foreach (var e in partTarget.GetComponentsInChildren<SaccEntity>(true))
            {
                if (e != bossEntity)
                    childTurrets.Add(e);
            }

            var linkSO = new SerializedObject(link);
            linkSO.Update();

            linkSO.FindProperty("PartTarget").objectReferenceValue = partTarget;

            var ctProp = linkSO.FindProperty("ChildTurrets");
            ctProp.arraySize = childTurrets.Count;
            for (int i = 0; i < childTurrets.Count; i++)
                ctProp.GetArrayElementAtIndex(i).objectReferenceValue = childTurrets[i];

            linkSO.ApplyModifiedProperties();

            // SaccTarget.ExplodeOther に BossPartLink を登録
            EnsureInArray(partTarget, "ExplodeOther", link);

            return true;
        }

        // ====================================================================
        //  バリデーション
        // ====================================================================

        private List<string> Validate(
            SaccEntity bossEntity,
            List<SaccTarget> parts,
            List<BossDamageRelay> relays)
        {
            var warnings = new List<string>();

            // ボス本体の DisableBulletHitEvent チェック
            if (!bossEntity.DisableBulletHitEvent)
            {
                warnings.Add(
                    $"  {bossEntity.name}: DisableBulletHitEvent = false\n" +
                    "    → 本体コライダーへの直接ダメージを無効化するには true に設定してください");
            }

            // パーツの RespawnDelay チェック
            foreach (var part in parts)
            {
                if (part.RespawnDelay > 0)
                {
                    warnings.Add(
                        $"  {part.name}: RespawnDelay = {part.RespawnDelay}\n" +
                        "    → BossController が一括管理するため 0 に設定してください");
                }
            }

            // 弱点の Rigidbody チェック
            foreach (var relay in relays)
            {
                var rb = relay.GetComponent<Rigidbody>();
                if (rb == null)
                {
                    warnings.Add(
                        $"  {relay.name}: Rigidbody なし\n" +
                        "    → OnParticleCollision を受けるには IsKinematic の Rigidbody が必須です");
                }
                else if (!rb.isKinematic)
                {
                    warnings.Add(
                        $"  {relay.name}: Rigidbody.isKinematic = false\n" +
                        "    → true に設定してください");
                }
            }

            return warnings;
        }

        // ====================================================================
        //  ユーティリティ
        // ====================================================================

        /// <summary>
        /// SerializedProperty の配列にコンポーネントリストを設定する。
        /// </summary>
        private void SetArrayProperty<T>(string propName, List<T> items) where T : Component
        {
            var prop = serializedObject.FindProperty(propName);
            prop.arraySize = items.Count;
            for (int i = 0; i < items.Count; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
        }

        /// <summary>
        /// 対象オブジェクトの配列フィールドにアイテムが未登録であれば末尾に追加する。
        /// </summary>
        private static void EnsureInArray(Component owner, string arrayFieldName, Component item)
        {
            var so = new SerializedObject(owner);
            so.Update();
            var prop = so.FindProperty(arrayFieldName);
            if (prop == null || !prop.isArray) return;

            // 既に含まれているかチェック
            for (int i = 0; i < prop.arraySize; i++)
            {
                if (prop.GetArrayElementAtIndex(i).objectReferenceValue == item)
                    return;
            }

            // 末尾に追加
            int idx = prop.arraySize;
            prop.InsertArrayElementAtIndex(idx);
            prop.GetArrayElementAtIndex(idx).objectReferenceValue = item;
            so.ApplyModifiedProperties();
        }

        /// <summary>
        /// 自身から親方向にヒエラルキーを辿り、指定コンポーネントを探す。
        /// </summary>
        private static T FindInSelfOrAncestors<T>(Transform t) where T : Component
        {
            while (t != null)
            {
                T c = t.GetComponent<T>();
                if (c != null) return c;
                t = t.parent;
            }
            return null;
        }
    }
}

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    [CustomEditor(typeof(DenLattice))]
    internal class DenLatticeInspector : UnityEditor.Editor
    {
        private SerializedProperty _edits;
        private SerializedProperty _interpolation;
        private SerializedProperty _mirror;
        private SerializedProperty _mirrorAxis;
        private SerializedProperty _bakeAsBlendShape;
        private SerializedProperty _blendShapeName;

        // バージョン表記 + 更新チェックの結果。State は保持せず表示のたびに
        // 「現在のローカル版 vs 取得済みの最新版」で再計算した値を受け取る。
        private DennokoVersionChecker.Result _versionResult;
        private static GUIStyle _versionLinkStyle;

        /// <summary>
        /// 直近にメニューが実行された時刻。多重実行の抑止に使う（<see cref="AddDenLatticeMenuItem"/>）。
        /// </summary>
        private static double _lastMenuInvokeTime = double.NegativeInfinity;

        /// <summary>
        /// 同一のメニュー実行による連続呼び出しとみなす間隔（秒）。
        /// Unity 側の呼び出しは同一フレーム内で連続するため、手で 2 回実行できない程度の値で足りる。
        /// </summary>
        private const double MenuReentryGuardSeconds = 0.2;

        [MenuItem("GameObject/dennokoworks/Dennoko Lattice", false, 20)]
        private static void AddDenLatticeMenuItem(MenuCommand menuCommand)
        {
            // Unity は複数選択中に GameObject メニューを実行すると、選択オブジェクトの数だけ
            // 同じメニュー項目を連続で呼び出す。ここでは最初の 1 回で選択全体をまとめて処理するので、
            // 直後に続く呼び出しは捨てる（そうしないと選択の数だけコンポーネントが増える）。
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastMenuInvokeTime < MenuReentryGuardSeconds) return;
            _lastMenuInvokeTime = now;

            var renderers = CollectSelectedRenderers();
            var target = ResolveMenuTarget(menuCommand, renderers);

            if (target == null)
            {
                target = new GameObject("DenLattice");
                Undo.RegisterCreatedObjectUndo(target, "Create DenLattice");
                GameObjectUtility.SetParentAndAlign(target, menuCommand.context as GameObject);
            }

            var component = target.GetComponent<DenLattice>();
            if (component == null)
            {
                component = Undo.AddComponent<DenLattice>(target);
            }

            // 付与先が自分のメッシュを持つなら、それを対象リストの先頭に置く。
            // コンポーネントが載っているオブジェクト自身のメッシュが 1 番目に並ぶ方が読みやすい。
            var ownRenderer = target.GetComponent<Renderer>();
            if (IsEditableRenderer(ownRenderer))
            {
                renderers.Remove(ownRenderer);
                renderers.Insert(0, ownRenderer);
            }

            var recorded = false;
            foreach (var renderer in renderers)
            {
                // Reset() による自動登録や、既存コンポーネントに設定済みの対象と重複させない
                if (component.FindEdit(renderer) != null) continue;

                if (!recorded)
                {
                    // グループは切らない。直前の AddComponent と同じ 1 段に入れて、
                    // Ctrl+Z 一回で「追加する前」へ戻れるようにする
                    DenLatticeUndo.Record(component, "Add Target to DenLattice");
                    recorded = true;
                }

                component.edits.Add(new MeshEdit { target = renderer });
            }

            if (recorded)
            {
                DenLatticeUndo.Apply(component);
            }

            Selection.activeGameObject = target;

            // どのオブジェクトに付いたのかを一瞬だけ色で示す
            HierarchyHighlight.Flash(target);

            if (HasEditableMesh(component))
            {
                EditSession.Begin(component);
            }
        }

        /// <summary>
        /// 選択中の GameObject から、変形対象になり得る Renderer を選択順に集める。
        /// </summary>
        private static List<Renderer> CollectSelectedRenderers()
        {
            var result = new List<Renderer>();

            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;

                // Project ビューで選択されたプレハブアセットはシーン上の変形対象にならない
                if (EditorUtility.IsPersistent(go)) continue;

                var renderer = go.GetComponent<Renderer>();
                if (!IsEditableRenderer(renderer)) continue;
                if (result.Contains(renderer)) continue;

                result.Add(renderer);
            }

            return result;
        }

        /// <summary>
        /// コンポーネントを付ける GameObject を決める。
        ///
        /// <see cref="MenuCommand.context"/> にはメニュー実行の起点になったオブジェクトが入る
        /// （ヒエラルキーの右クリックならクリックしたオブジェクト）。それが変形対象に含まれていれば
        /// それを使い、使えなければ「最後に触ったもの」である <see cref="Selection.activeGameObject"/>、
        /// それも対象外なら選択順で最初の対象へ落とす。
        /// </summary>
        private static GameObject ResolveMenuTarget(MenuCommand menuCommand, List<Renderer> renderers)
        {
            var context = menuCommand.context as GameObject;

            if (renderers.Count > 0)
            {
                if (Contains(renderers, context)) return context;
                if (Contains(renderers, Selection.activeGameObject)) return Selection.activeGameObject;

                return renderers[0].gameObject;
            }

            // メッシュを 1 つも選んでいない場合は、従来どおり起点のオブジェクトへ付ける
            return context != null ? context : Selection.activeGameObject;
        }

        private static bool Contains(List<Renderer> renderers, GameObject go)
        {
            if (go == null) return false;

            foreach (var renderer in renderers)
            {
                if (renderer.gameObject == go) return true;
            }

            return false;
        }

        private static bool IsEditableRenderer(Renderer renderer)
        {
            return renderer is SkinnedMeshRenderer || renderer is MeshRenderer;
        }

        /// <summary>
        /// 追加スロットから変形対象にできる Renderer か。
        ///
        /// スロットは Project ビューからのドロップも受け取れてしまう（<c>ObjectField</c> は
        /// シーンとアセットの両方を許可する）。プレハブアセットの Renderer を登録しても
        /// シーン上には何も現れず、変形がアセット側を向いてしまうので、右クリック追加
        /// （→ <see cref="CollectSelectedRenderers"/>）と同じくここで落とす。
        /// </summary>
        private static bool IsAddableRenderer(Renderer renderer)
        {
            if (!IsEditableRenderer(renderer)) return false;

            return !EditorUtility.IsPersistent(renderer);
        }

        /// <summary>変形を開始できる（メッシュを取得できる）対象が 1 つでもあるか。</summary>
        private static bool HasEditableMesh(DenLattice component)
        {
            foreach (var edit in component.edits)
            {
                if (edit != null && MeshDeltaApplier.GetSharedMesh(edit.target) != null) return true;
            }

            return false;
        }

        private void OnEnable()
        {
            _edits = serializedObject.FindProperty("edits");
            _interpolation = serializedObject.FindProperty("interpolation");
            _mirror = serializedObject.FindProperty("mirror");
            _mirrorAxis = serializedObject.FindProperty("mirrorAxis");
            _bakeAsBlendShape = serializedObject.FindProperty("bakeAsBlendShape");
            _blendShapeName = serializedObject.FindProperty("blendShapeName");

            DenLatticeLocalization.OnLanguageChanged += OnLanguageChanged;

            // 前回の取得結果を反映しつつ、未取得／前回エラーなら取得を開始する。
            // Inspector を選び直すたびに一時的な取得失敗から自己回復できる。
            ReloadVersionResult();
            DenLatticeVersion.StartCheckBackgroundTask();

            // 選択アウトラインを戻しそこねていた場合の自己回復。判定に IsActive(target) を
            // 使わないのは、別の対象へ選択を移した直後はまだ前のセッションが生きているため。
            if (EditSession.Active == null)
            {
                SelectionOutline.Restore();
            }
        }

        private void OnDisable()
        {
            DenLatticeLocalization.OnLanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged()
        {
            Repaint();
        }

        /// <summary>取得完了時に <see cref="DenLatticeVersion"/> から呼ばれる。</summary>
        internal void ReloadVersionResult()
        {
            _versionResult = DenLatticeVersion.LoadResultFromSessionState();
            Repaint();
        }

        /// <summary>
        /// 編集セッション中は、シーンビュー側の操作がコンポーネントへ反映されるので
        /// Inspector も追従させる。
        /// </summary>
        public override bool RequiresConstantRepaint()
        {
            return target is DenLattice component && EditSession.IsActive(component);
        }

        public override void OnInspectorGUI()
        {
            var component = (DenLattice)target;
            serializedObject.Update();

            DrawVersionBar();
            EditorGUILayout.Space();
            DrawTargets();
            EditorGUILayout.Space();
            DrawEditControls(component);
            EditorGUILayout.Space();
            DrawLatticeSettings(component);
            EditorGUILayout.Space();
            DrawMirrorSettings();
            EditorGUILayout.Space();
            DrawBakeSection(component);

            // 補間方式・ミラー軸などは PropertyField 経由で書き換わるため、ここで拾わないと
            // 編集セッションが古い設定のまま描画を続ける
            if (serializedObject.ApplyModifiedProperties() && EditSession.IsActive(component))
            {
                EditSession.NotifySettingsChanged();
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// 一番上の 1 行。左に現在のバージョン、その隣に更新状態、右端に再確認ボタンと言語切り替えボタン。
        /// 「更新あり」のときだけクリックでダウンロードページを開く。
        /// </summary>
        private void DrawVersionBar()
        {
            if (_versionLinkStyle == null)
            {
                _versionLinkStyle = new GUIStyle(EditorStyles.miniLabel);
            }

            var prevColor = GUI.contentColor;

            EditorGUILayout.BeginHorizontal();

            GUI.contentColor = new Color(0.68f, 0.68f, 0.68f);
            GUILayout.Label($"v{_versionResult.LocalVersion}", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            GUI.contentColor = prevColor;

            switch (_versionResult.State)
            {
                case DennokoVersionChecker.State.UpdateAvailable:
                {
                    var tooltip = string.IsNullOrEmpty(_versionResult.Message)
                        ? DenLatticeLocalization.Tr("version.update_tooltip")
                        : _versionResult.Message;

                    GUI.contentColor = new Color(0.35f, 0.8f, 0.4f);
                    var clicked = GUILayout.Button(
                        new GUIContent(
                            DenLatticeLocalization.Format("version.update_available", _versionResult.LatestVersion),
                            tooltip),
                        _versionLinkStyle, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;

                    EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                    if (clicked)
                    {
                        DenLatticeVersion.OpenUpdatePage(_versionResult.Url);
                    }

                    break;
                }

                case DennokoVersionChecker.State.Error:
                    GUI.contentColor = new Color(1f, 0.72f, 0.3f);
                    GUILayout.Label(
                        new GUIContent(DenLatticeLocalization.Tr("version.error"),
                            DenLatticeLocalization.Tr("version.error_tooltip")),
                        EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                case DennokoVersionChecker.State.Checking:
                    GUI.contentColor = new Color(0.55f, 0.55f, 0.55f);
                    GUILayout.Label(DenLatticeLocalization.Tr("version.checking"), EditorStyles.miniLabel,
                        GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                default: // UpToDate — バージョン表記だけ
                    break;
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(new GUIContent("↻", DenLatticeLocalization.Tr("version.recheck_tooltip")),
                    EditorStyles.miniButton, GUILayout.Width(22)))
            {
                DenLatticeVersion.ForceRecheck();
                ReloadVersionResult(); // 即座に「確認中...」表示へ
            }

            if (GUILayout.Button(
                    new GUIContent(DenLatticeLocalization.ButtonText, DenLatticeLocalization.ButtonTooltip),
                    EditorStyles.miniButton, GUILayout.Width(28)))
            {
                DenLatticeLocalization.ToggleLanguage();
            }

            EditorGUILayout.EndHorizontal();

            var separator = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(separator, new Color(0f, 0f, 0f, 0.2f));
        }

        // ------------------------------------------------------------------

        private void DrawTargets()
        {
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.targets_header"), EditorStyles.boldLabel);

            var removeAt = -1;

            for (var i = 0; i < _edits.arraySize; i++)
            {
                var element = _edits.GetArrayElementAtIndex(i);
                var targetProp = element.FindPropertyRelative("target");
                var countProp = element.FindPropertyRelative("count");
                var vertexCountProp = element.FindPropertyRelative("vertexCount");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(targetProp, GUIContent.none);

                GUILayout.Label(DenLatticeLocalization.Format("inspector.vertex_count", countProp.intValue),
                    GUILayout.Width(64));

                if (GUILayout.Button("×", GUILayout.Width(22)))
                {
                    removeAt = i;
                }

                EditorGUILayout.EndHorizontal();

                DrawTargetWarning(targetProp, vertexCountProp);
            }

            if (removeAt >= 0)
            {
                _edits.DeleteArrayElementAtIndex(removeAt);
            }

            DrawAddTargetSlot();

            if (_edits.arraySize == 0)
            {
                EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.add_target_prompt"), MessageType.Info);
            }
        }

        /// <summary>
        /// 常設の追加用スロット。ここへ Renderer を持つオブジェクトをドラッグ＆ドロップすると、
        /// そのまま変形対象へ加わる。
        ///
        /// 以前は「対象を追加」ボタンで空の要素を作ってから割り当てる 2 手が必要だった。
        /// スロットは常に空のまま（値を保持しない）なので、続けて何個でも放り込める。
        /// </summary>
        private void DrawAddTargetSlot()
        {
            var rect = EditorGUILayout.GetControlRect();

            // 複数同時のドロップは ObjectField が扱えない（1 個しか受け取らない）ので、
            // ObjectField へ渡る前に自前で処理する
            HandleMultiDrop(rect);

            var label = new GUIContent(
                DenLatticeLocalization.Tr("inspector.add_target"),
                DenLatticeLocalization.Tr("inspector.add_target_tooltip"));

            // 表示値は常に null。追加した対象は上のリストへ並ぶので、スロットに残す意味がない
            var picked = EditorGUI.ObjectField(rect, label, null, typeof(Renderer), true) as Renderer;
            if (picked == null) return;

            AddTargets(new List<Renderer> { picked });
        }

        /// <summary>
        /// スロットへの複数同時ドロップを処理する。1 個だけのドラッグは <c>ObjectField</c> へ任せ、
        /// 枠のハイライトなどを Unity 標準の見た目のままにする。
        /// </summary>
        private void HandleMultiDrop(Rect rect)
        {
            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (DragAndDrop.objectReferences.Length <= 1) return;
            if (!rect.Contains(evt.mousePosition)) return;

            var renderers = CollectDroppedRenderers(DragAndDrop.objectReferences);
            if (renderers.Count == 0) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Link;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddTargets(renderers);
            }

            evt.Use();
        }

        /// <summary>
        /// ドロップされたオブジェクトから変形対象になり得る Renderer を取り出す。
        /// メッシュを持たない Renderer（パーティクルなど）・プレハブアセット・重複は落とす。
        /// </summary>
        private static List<Renderer> CollectDroppedRenderers(Object[] dropped)
        {
            var result = new List<Renderer>();

            foreach (var obj in dropped)
            {
                var renderer = obj as Renderer;
                if (renderer == null && obj is GameObject go)
                {
                    renderer = go.GetComponent<Renderer>();
                }

                if (!IsAddableRenderer(renderer)) continue;
                if (result.Contains(renderer)) continue;

                result.Add(renderer);
            }

            return result;
        }

        /// <summary>
        /// 変形対象へ追加する。既に登録済みのものは飛ばし、実際に増えたときだけ Undo を 1 段積む。
        /// </summary>
        private void AddTargets(List<Renderer> renderers)
        {
            // arraySize++ は直前の要素を複製する。変形データ（byte[] blob）まで
            // 引き継がれると厄介なので、SerializedProperty で個別に潰すのではなく
            // 素の MeshEdit を直接追加する
            serializedObject.ApplyModifiedProperties();

            var component = (DenLattice)target;
            var added = false;

            foreach (var renderer in renderers)
            {
                if (!IsAddableRenderer(renderer)) continue;
                if (component.FindEdit(renderer) != null) continue;

                if (!added)
                {
                    DenLatticeUndo.BeginGroup(component, "Add Dennoko Lattice Target");
                    added = true;
                }

                component.edits.Add(new MeshEdit { target = renderer });
            }

            if (added)
            {
                DenLatticeUndo.Apply(component);
                DenLatticeUndo.EndGroup();
            }

            serializedObject.Update();
        }

        private static void DrawTargetWarning(SerializedProperty targetProp, SerializedProperty vertexCountProp)
        {
            var renderer = targetProp.objectReferenceValue as Renderer;
            if (renderer == null) return;

            var mesh = MeshDeltaApplier.GetSharedMesh(renderer);
            if (mesh == null)
            {
                EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.warn_no_mesh"), MessageType.Warning);
                return;
            }

            var recorded = vertexCountProp.intValue;
            if (recorded != 0 && recorded != mesh.vertexCount)
            {
                EditorGUILayout.HelpBox(
                    DenLatticeLocalization.Format("inspector.warn_vertex_count_mismatch", mesh.vertexCount, recorded),
                    MessageType.Error);
            }
        }

        // ------------------------------------------------------------------

        private void DrawEditControls(DenLattice component)
        {
            var editing = EditSession.IsActive(component);

            using (new EditorGUI.DisabledScope(!editing && _edits.arraySize == 0))
            {
                var prevColor = GUI.backgroundColor;
                if (editing)
                {
                    GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                    if (GUILayout.Button(DenLatticeLocalization.Tr("inspector.btn_edit_end"), GUILayout.Height(28)))
                    {
                        EditSession.End();
                    }
                }
                else
                {
                    GUI.backgroundColor = new Color(0.35f, 0.75f, 1f);
                    if (GUILayout.Button(DenLatticeLocalization.Tr("inspector.btn_edit_start"), GUILayout.Height(28)))
                    {
                        Selection.activeGameObject = component.gameObject;
                        serializedObject.ApplyModifiedProperties();
                        EditSession.Begin(component);
                    }
                }

                GUI.backgroundColor = prevColor;
            }

            if (!editing) return;

            EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.edit_help"), MessageType.Info);

            var session = EditSession.Active;

            if (session != null && session.AnyVertexCountMismatch)
            {
                EditorGUILayout.HelpBox(
                    DenLatticeLocalization.Tr("inspector.warn_ndmf_vertex_mismatch"), MessageType.Warning);
            }
            else if (session != null && session.ShowFallbackWarning)
            {
                EditorGUILayout.HelpBox(
                    DenLatticeLocalization.Tr("inspector.warn_ndmf_fallback"), MessageType.Warning);
            }
        }

        // ------------------------------------------------------------------

        private void DrawLatticeSettings(DenLattice component)
        {
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.lattice_header"), EditorStyles.boldLabel);

            // 格子数は private フィールドなので SerializedProperty では書かない。
            // 変更には制御点の作り直しが伴うため、必ず LatticeResampler を通す。
            // ラベルの行を分けるのは、PrefixLabel と同じ行に置くと Inspector を狭めたときに
            // 3 軸分（270px）が入りきらず ± ボタンが潰れるため
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.resolution"));

            var resolution = new Vector3Int(component.ResU, component.ResV, component.ResW);
            var nextResolution = LatticeResolutionField.Draw(resolution);

            if (nextResolution != resolution)
            {
                serializedObject.ApplyModifiedProperties();

                if (EditSession.IsActive(component))
                {
                    EditSession.Active.ChangeResolution(nextResolution.x, nextResolution.y, nextResolution.z);
                }
                else
                {
                    LatticeResampler.ChangeResolution(component, nextResolution.x, nextResolution.y, nextResolution.z);
                }

                serializedObject.Update();
            }

            EditorGUILayout.PropertyField(_interpolation,
                new GUIContent(DenLatticeLocalization.Tr("inspector.interpolation"),
                    DenLatticeLocalization.Tr("inspector.interpolation_tooltip")));

            EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.resolution_help"), MessageType.None);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.box_header"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.box_help"), MessageType.None);

            DrawBoxPlacementButtons(component);

            using (new EditorGUI.DisabledScope(!component.HasControlOffsets))
            {
                if (GUILayout.Button(DenLatticeLocalization.Tr("inspector.btn_reset_cage")))
                {
                    serializedObject.ApplyModifiedProperties();

                    if (EditSession.IsActive(component)) EditSession.Active.ResetControlPoints();
                    else LatticeResampler.ResetCage(component);

                    serializedObject.Update();
                }
            }
        }

        /// <summary>
        /// ボックスの自動配置。どちらも「対象の頂点がいまどこにあるか」を必要とするため、
        /// 編集セッション（＝NDMF プロキシ）が生きている間だけ押せる。
        /// </summary>
        private void DrawBoxPlacementButtons(DenLattice component)
        {
            var session = EditSession.IsActive(component) ? EditSession.Active : null;

            var fit = false;
            var center = false;

            using (new EditorGUI.DisabledScope(session == null))
            {
                EditorGUILayout.BeginHorizontal();
                fit = GUILayout.Button(DenLatticeLocalization.Tr("inspector.fit_box"));
                center = GUILayout.Button(DenLatticeLocalization.Tr("inspector.center_box"));
                EditorGUILayout.EndHorizontal();
            }

            if (session == null || (!fit && !center)) return;

            // セッションはコンポーネントを直接書き換えるので、SerializedObject の
            // 読み書きの間に挟まないよう前後で同期する
            serializedObject.ApplyModifiedProperties();

            if (fit) session.AutoFitBox();
            else session.CenterBoxOnMirrorPlane();

            serializedObject.Update();
        }

        // ------------------------------------------------------------------

        private void DrawMirrorSettings()
        {
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.mirror_header"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(DenLatticeLocalization.Tr("inspector.mirror_axis"));

            var currentAxis = (LatticeAxis)_mirrorAxis.enumValueIndex;
            var isMirrorActive = _mirror.boolValue;
            var defaultColor = GUI.backgroundColor;
            var activeColor = new Color(0.35f, 0.95f, 0.45f);

            var axes = new[] { LatticeAxis.U, LatticeAxis.V, LatticeAxis.W };
            var labels = new[] { "X", "Y", "Z" };

            for (var i = 0; i < axes.Length; i++)
            {
                var isSelected = isMirrorActive && currentAxis == axes[i];
                GUI.backgroundColor = isSelected ? activeColor : defaultColor;

                if (GUILayout.Button(labels[i], GUILayout.Height(24)))
                {
                    if (isSelected)
                    {
                        _mirror.boolValue = false;
                    }
                    else
                    {
                        _mirror.boolValue = true;
                        _mirrorAxis.enumValueIndex = (int)axes[i];
                    }

                    serializedObject.ApplyModifiedProperties();
                    EditSession.NotifySettingsChanged();
                }
            }

            GUI.backgroundColor = defaultColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.mirror_help"), MessageType.None);
        }

        // ------------------------------------------------------------------

        private void DrawBakeSection(DenLattice component)
        {
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.bake_header"), EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_bakeAsBlendShape,
                new GUIContent(DenLatticeLocalization.Tr("inspector.bake_as_blendshape"),
                    DenLatticeLocalization.Tr("inspector.bake_as_blendshape_tooltip")));

            if (_bakeAsBlendShape.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(_blendShapeName,
                        new GUIContent(DenLatticeLocalization.Tr("inspector.blendshape_name"),
                            DenLatticeLocalization.Tr("inspector.blendshape_name_tooltip")));
                }
            }

            EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.bake_help"), MessageType.None);

            var hasEdits = false;
            foreach (var edit in component.edits)
            {
                if (edit == null || edit.target == null || !edit.HasEdits) continue;
                hasEdits = true;
                break;
            }

            using (new EditorGUI.DisabledScope(!hasEdits))
            {
                if (GUILayout.Button(DenLatticeLocalization.Tr("inspector.btn_bake"), GUILayout.Height(24)))
                {
                    serializedObject.ApplyModifiedProperties();
                    DenLatticeBaker.Bake(component);
                }
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(!hasEdits && !component.HasControlOffsets))
            {
                if (!GUILayout.Button(DenLatticeLocalization.Tr("inspector.btn_clear_all"))) return;

                if (!EditorUtility.DisplayDialog(
                        DenLatticeLocalization.Tr("inspector.dialog_clear_title"),
                        DenLatticeLocalization.Tr("inspector.dialog_clear_message"),
                        DenLatticeLocalization.Tr("inspector.dialog_clear_ok"),
                        DenLatticeLocalization.Tr("inspector.dialog_clear_cancel")))
                {
                    return;
                }

                EditSession.End();

                DenLatticeUndo.BeginGroup(component, "Clear Dennoko Lattice Deformation");
                foreach (var edit in component.edits)
                {
                    edit?.Clear();
                }

                component.ClearControlOffsets();

                DenLatticeUndo.Apply(component);
                DenLatticeUndo.EndGroup();

                LiveEdits.Invalidate();

                // SerializedObject を経由せずに書き換えたので、
                // 末尾の ApplyModifiedProperties が古い値を書き戻さないよう読み直す
                serializedObject.Update();
            }
        }
    }
}

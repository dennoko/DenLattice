using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    [CustomEditor(typeof(DenLattice))]
    internal class DenLatticeInspector : UnityEditor.Editor
    {
        private SerializedProperty _edits;
        private SerializedProperty _interpolation;
        private SerializedProperty _freezeBorder;
        private SerializedProperty _mirror;
        private SerializedProperty _mirrorAxis;
        private SerializedProperty _bakeAsBlendShape;
        private SerializedProperty _blendShapeName;

        // バージョン表記 + 更新チェックの結果。State は保持せず表示のたびに
        // 「現在のローカル版 vs 取得済みの最新版」で再計算した値を受け取る。
        private DennokoVersionChecker.Result _versionResult;
        private static GUIStyle _versionLinkStyle;

        [MenuItem("GameObject/dennokoworks/Dennoko Lattice", false, 20)]
        private static void AddDenLatticeMenuItem(MenuCommand menuCommand)
        {
            var target = menuCommand.context as GameObject ?? Selection.activeGameObject;

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

            Selection.activeGameObject = target;

            var renderer = target.GetComponent<Renderer>();
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) return;

            if (component.edits.Count == 0)
            {
                Undo.RecordObject(component, "Add Target to DenLattice");
                component.edits.Add(new MeshEdit { target = renderer });
                EditorUtility.SetDirty(component);
            }

            if (MeshDeltaApplier.GetSharedMesh(renderer) != null)
            {
                EditSession.Begin(component);
            }
        }

        private void OnEnable()
        {
            _edits = serializedObject.FindProperty("edits");
            _interpolation = serializedObject.FindProperty("interpolation");
            _freezeBorder = serializedObject.FindProperty("freezeBorder");
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

            if (GUILayout.Button(DenLatticeLocalization.Tr("inspector.add_target")))
            {
                // arraySize++ は直前の要素を複製する。変形データ（byte[] blob）まで
                // 引き継がれると厄介なので、素の MeshEdit を直接追加する
                serializedObject.ApplyModifiedProperties();

                var component = (DenLattice)target;
                Undo.RecordObject(component, "Add Dennoko Lattice Target");
                component.edits.Add(new MeshEdit());
                EditorUtility.SetDirty(component);

                if (PrefabUtility.IsPartOfPrefabInstance(component))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }

                serializedObject.Update();
            }

            if (_edits.arraySize == 0)
            {
                EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.add_target_prompt"), MessageType.Info);
            }
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
            // 変更には旧い変位場のリサンプルが伴うため、必ず LatticeResampler を通す
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(DenLatticeLocalization.Tr("inspector.resolution"));

            EditorGUI.BeginChangeCheck();
            var u = EditorGUILayout.DelayedIntField(component.ResU);
            var v = EditorGUILayout.DelayedIntField(component.ResV);
            var w = EditorGUILayout.DelayedIntField(component.ResW);
            var resolutionChanged = EditorGUI.EndChangeCheck();

            EditorGUILayout.EndHorizontal();

            if (resolutionChanged)
            {
                serializedObject.ApplyModifiedProperties();

                if (EditSession.IsActive(component)) EditSession.Active.ChangeResolution(u, v, w);
                else LatticeResampler.ChangeResolution(component, u, v, w);

                serializedObject.Update();
            }

            EditorGUILayout.PropertyField(_interpolation,
                new GUIContent(DenLatticeLocalization.Tr("inspector.interpolation"),
                    DenLatticeLocalization.Tr("inspector.interpolation_tooltip")));

            EditorGUILayout.PropertyField(_freezeBorder,
                new GUIContent(DenLatticeLocalization.Tr("inspector.freeze_border"),
                    DenLatticeLocalization.Tr("inspector.freeze_border_tooltip")));

            EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.resolution_help"), MessageType.None);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.box_header"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.box_help"), MessageType.None);

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

        // ------------------------------------------------------------------

        private void DrawMirrorSettings()
        {
            var mirrorActive = _mirror.boolValue;
            var prevColor = GUI.backgroundColor;
            if (mirrorActive) GUI.backgroundColor = new Color(0.35f, 0.95f, 0.45f);

            var buttonText = mirrorActive
                ? DenLatticeLocalization.Tr("inspector.mirror_on")
                : DenLatticeLocalization.Tr("inspector.mirror_off");

            if (GUILayout.Button(buttonText, GUILayout.Height(26)))
            {
                _mirror.boolValue = !mirrorActive;
                serializedObject.ApplyModifiedProperties();
                EditSession.NotifySettingsChanged();
            }

            GUI.backgroundColor = prevColor;

            using (new EditorGUI.DisabledScope(!_mirror.boolValue))
            {
                DrawMirrorAxisButtons();
            }

            if (_mirror.boolValue)
            {
                EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("inspector.mirror_help"), MessageType.None);
            }
        }

        private void DrawMirrorAxisButtons()
        {
            EditorGUILayout.LabelField(DenLatticeLocalization.Tr("inspector.mirror_axis"));
            EditorGUILayout.BeginHorizontal();

            var currentAxis = (LatticeAxis)_mirrorAxis.enumValueIndex;
            var defaultColor = GUI.backgroundColor;
            var selectedColor = new Color(0.35f, 0.7f, 1f);

            var axes = new[] { LatticeAxis.U, LatticeAxis.V, LatticeAxis.W };
            var labels = new[] { "U (X)", "V (Y)", "W (Z)" };

            for (var i = 0; i < axes.Length; i++)
            {
                var isSelected = currentAxis == axes[i];
                GUI.backgroundColor = isSelected ? selectedColor : defaultColor;

                if (GUILayout.Button(labels[i], GUILayout.Height(24)) && !isSelected)
                {
                    _mirrorAxis.enumValueIndex = (int)axes[i];
                    serializedObject.ApplyModifiedProperties();
                    EditSession.NotifySettingsChanged();
                }
            }

            GUI.backgroundColor = defaultColor;
            EditorGUILayout.EndHorizontal();
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

                Undo.RecordObject(component, "Clear Dennoko Lattice Deformation");
                foreach (var edit in component.edits)
                {
                    edit?.Clear();
                }

                component.ClearControlOffsets();

                EditorUtility.SetDirty(component);
                if (PrefabUtility.IsPartOfPrefabInstance(component))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }

                LiveEdits.Invalidate();

                // SerializedObject を経由せずに書き換えたので、
                // 末尾の ApplyModifiedProperties が古い値を書き戻さないよう読み直す
                serializedObject.Update();
            }
        }
    }
}

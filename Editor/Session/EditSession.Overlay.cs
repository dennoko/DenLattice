using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    internal partial class EditSession
    {
        private Rect _overlayRect;
        private static bool _hasCustomOverlayPosition;
        private static Vector2 _overlayPosition;
        private static bool _overlayDragging;
        private static Vector2 _overlayDragOffset;

        // Layout イベント時に固定する、レイアウト構成に影響する状態
        private bool _overlayBoxMode;
        private bool _overlayMirror;

        private void DrawOverlay(SceneView sceneView)
        {
            // Layout と Repaint で GUILayout の構成が変わると
            // 「Getting control N's position in a group with only M controls」で例外になる。
            // Refresh は毎イベント走って警告状態を書き換えるため、レイアウトに影響する状態は
            // Layout イベント時に固定してから両イベントで使い回す。
            if (Event.current.type == EventType.Layout)
            {
                _overlayShowsWarning = _showFallbackWarning;
                _overlayBoxMode = _boxMode;
                _overlayMirror = _component.mirror;
            }

            var current = Event.current;

            var overlayHeight = 318f
                                + (_overlayMirror ? 26f : 0f)
                                + (_overlayBoxMode ? 26f : 0f)
                                + (_overlayShowsWarning ? 44f : 0f);
            const float overlayWidth = 340f;
            const float margin = 10f;

            var canvasWidth = GetCanvasWidth(sceneView);
            var canvasHeight = GetCanvasHeight(sceneView);

            var maxX = Mathf.Max(margin, canvasWidth - overlayWidth - margin);
            var maxY = Mathf.Max(margin, canvasHeight - overlayHeight - margin);

            if (!_hasCustomOverlayPosition)
            {
                // デフォルトは右下追従（ウィンドウのリサイズや比率変更にも追従）
                _overlayPosition.x = maxX;
                _overlayPosition.y = maxY;
            }
            else
            {
                _overlayPosition.x = Mathf.Clamp(_overlayPosition.x, margin, maxX);
                _overlayPosition.y = Mathf.Clamp(_overlayPosition.y, margin, maxY);
            }

            _overlayRect = new Rect(_overlayPosition.x, _overlayPosition.y, overlayWidth, overlayHeight);
            var headerRect = new Rect(_overlayRect.x, _overlayRect.y, _overlayRect.width, 24f);

            // ヘッダーのドラッグ移動
            if (current.type == EventType.MouseDown && current.button == 0 && headerRect.Contains(current.mousePosition))
            {
                _overlayDragging = true;
                _overlayDragOffset = current.mousePosition - _overlayPosition;
                _hasCustomOverlayPosition = true;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && _overlayDragging)
            {
                _overlayPosition = current.mousePosition - _overlayDragOffset;
                _overlayPosition.x = Mathf.Clamp(_overlayPosition.x, margin, maxX);
                _overlayPosition.y = Mathf.Clamp(_overlayPosition.y, margin, maxY);
                current.Use();
                GUI.changed = true;
            }
            else if ((current.type == EventType.MouseUp || current.rawType == EventType.MouseUp) && _overlayDragging)
            {
                _overlayDragging = false;
                current.Use();
            }

            Handles.BeginGUI();

            EditorGUI.DrawRect(_overlayRect, new Color(0.16f, 0.16f, 0.16f, 0.94f));
            DrawOutlineRect(_overlayRect, new Color(0.25f, 0.88f, 0.45f, 0.95f), 2f);

            EditorGUIUtility.AddCursorRect(headerRect, MouseCursor.MoveArrow);

            GUILayout.BeginArea(_overlayRect, GUIStyle.none);
            GUILayout.Space(6);

            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 84f;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            GUILayout.Label(DenLatticeLocalization.Tr("overlay.title"), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("⠿", EditorStyles.miniLabel);
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();

            DrawResolutionRow();
            DrawInterpolationRow();
            GUILayout.Space(3);
            DrawMirrorSection();
            GUILayout.Space(3);
            DrawBoxSection();
            GUILayout.Space(3);
            DrawHints();

            if (_overlayShowsWarning)
            {
                EditorGUILayout.HelpBox(DenLatticeLocalization.Tr("overlay.warn_ndmf_fallback"),
                    MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();

            EditorGUIUtility.labelWidth = previousLabelWidth;

            GUILayout.EndArea();
            Handles.EndGUI();

            FlushSettingsUndoGroup();
        }

        /// <summary>
        /// 格子数。<b>ここを変えても形状は変わらない</b>のが本ツールの中核で、
        /// だからこそ編集を中断せずに触れる位置へ置いている。
        /// </summary>
        private void DrawResolutionRow()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(DenLatticeLocalization.Tr("overlay.resolution"), GUILayout.Width(84));

            EditorGUI.BeginChangeCheck();
            // Delayed 版を使う。素の IntField は 1 文字打つたびに変更を出すので、
            // 「10」と入れようとすると途中の「1」でクランプされて打ち直しになる
            var u = EditorGUILayout.DelayedIntField(_component.ResU);
            var v = EditorGUILayout.DelayedIntField(_component.ResV);
            var w = EditorGUILayout.DelayedIntField(_component.ResW);
            if (EditorGUI.EndChangeCheck())
            {
                ChangeResolution(u, v, w);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawInterpolationRow()
        {
            EditorGUI.BeginChangeCheck();
            var interpolation = (InterpolationType)EditorGUILayout.EnumPopup(
                DenLatticeLocalization.Tr("overlay.interpolation"), _component.interpolation);
            var freeze = EditorGUILayout.Toggle(
                DenLatticeLocalization.Tr("overlay.freeze_border"), _component.freezeBorder);

            if (!EditorGUI.EndChangeCheck()) return;

            RecordSettingsChange();
            _component.interpolation = interpolation;
            _component.freezeBorder = freeze;
            EditorUtility.SetDirty(_component);

            // 境界固定は「動かせる制御点」の集合を変える。選択が固定点を含んだままだと
            // 掴めない点が選択されて見えるので、選び直させる
            if (freeze) PruneFrozenSelection();

            RecomputeCenter();
            BuildInfluences();
        }

        private void DrawMirrorSection()
        {
            var mirrorActive = _component.mirror;
            var previousColor = GUI.backgroundColor;
            if (mirrorActive) GUI.backgroundColor = new Color(0.35f, 0.95f, 0.45f);

            var label = mirrorActive
                ? DenLatticeLocalization.Tr("overlay.mirror_on")
                : DenLatticeLocalization.Tr("overlay.mirror_off");

            if (GUILayout.Button(label, GUILayout.Height(24)))
            {
                RecordSettingsChange();
                _component.mirror = !mirrorActive;
                EditorUtility.SetDirty(_component);

                BuildInfluences();
                SceneView.RepaintAll();
            }

            GUI.backgroundColor = previousColor;

            if (!_overlayMirror) return;

            EditorGUILayout.BeginHorizontal();

            var axes = new[] { LatticeAxis.U, LatticeAxis.V, LatticeAxis.W };
            var names = new[] { "U (X)", "V (Y)", "W (Z)" };

            for (var i = 0; i < axes.Length; i++)
            {
                var selected = _component.mirrorAxis == axes[i];
                GUI.backgroundColor = selected ? new Color(0.35f, 0.7f, 1f) : previousColor;

                if (GUILayout.Button(names[i], GUILayout.Height(20)) && !selected)
                {
                    RecordSettingsChange();
                    _component.mirrorAxis = axes[i];
                    EditorUtility.SetDirty(_component);

                    BuildInfluences();
                    SceneView.RepaintAll();
                }
            }

            GUI.backgroundColor = previousColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBoxSection()
        {
            var previousColor = GUI.backgroundColor;
            if (_boxMode) GUI.backgroundColor = new Color(1f, 0.75f, 0.3f);

            var label = _boxMode
                ? DenLatticeLocalization.Tr("overlay.box_mode_on")
                : DenLatticeLocalization.Tr("overlay.box_mode_off");

            if (GUILayout.Button(label, GUILayout.Height(24)))
            {
                BoxMode = !_boxMode;
            }

            GUI.backgroundColor = previousColor;

            if (_overlayBoxMode)
            {
                EditorGUILayout.BeginHorizontal();

                var tools = new[] { BoxTool.Move, BoxTool.Rotate, BoxTool.Scale };
                var names = new[]
                {
                    DenLatticeLocalization.Tr("overlay.box_move"),
                    DenLatticeLocalization.Tr("overlay.box_rotate"),
                    DenLatticeLocalization.Tr("overlay.box_scale"),
                };

                for (var i = 0; i < tools.Length; i++)
                {
                    var selected = _boxTool == tools[i];
                    GUI.backgroundColor = selected ? new Color(0.35f, 0.7f, 1f) : previousColor;

                    if (GUILayout.Button(names[i], GUILayout.Height(20)))
                    {
                        _boxTool = tools[i];
                        SceneView.RepaintAll();
                    }
                }

                GUI.backgroundColor = previousColor;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(DenLatticeLocalization.Tr("overlay.fit_box"), GUILayout.Height(20)))
            {
                AutoFitBox();
            }

            if (GUILayout.Button(DenLatticeLocalization.Tr("overlay.center_box"), GUILayout.Height(20)))
            {
                CenterBoxOnMirrorPlane();
            }

            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!_component.HasControlOffsets))
            {
                if (GUILayout.Button(DenLatticeLocalization.Tr("overlay.reset_cage"), GUILayout.Height(20)))
                {
                    ResetControlPoints();
                }
            }
        }

        private static void DrawHints()
        {
            var style = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                normal = { textColor = new Color(0.3f, 0.95f, 0.45f) }
            };

            GUILayout.Label(DenLatticeLocalization.Tr("overlay.hint_select"), style);
            GUILayout.Label(DenLatticeLocalization.Tr("overlay.hint_esc"), style);
        }

        /// <summary>境界固定を有効にしたときに、動かせなくなった制御点を選択から外す。</summary>
        private void PruneFrozenSelection()
        {
            _selected.RemoveWhere(index => !IsMovable(index));
            if (!_hasSelection) ClearSelection();
        }

        /// <summary>
        /// オーバーレイでの設定変更を Undo 1 段にまとめる。
        ///
        /// ここでグループを切らないと、連続した設定変更どうしが 1 段に潰れて
        /// Ctrl+Z で一気に巻き戻る。まとめ上げは MouseUp のタイミングで行う。
        /// </summary>
        private void RecordSettingsChange()
        {
            if (_settingsUndoGroup < 0)
            {
                Undo.IncrementCurrentGroup();
                _settingsUndoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Dennoko Lattice Settings");
            }

            Undo.RecordObject(_component, "Dennoko Lattice Settings");
        }

        private void FlushSettingsUndoGroup()
        {
            if (_settingsUndoGroup < 0) return;
            if (Event.current == null || Event.current.rawType != EventType.MouseUp) return;

            Undo.CollapseUndoOperations(_settingsUndoGroup);
            _settingsUndoGroup = -1;
        }

        private static float GetCanvasWidth(SceneView sceneView)
        {
            if (sceneView != null && sceneView.camera != null)
            {
                var ppp = EditorGUIUtility.pixelsPerPoint;
                if (ppp > 0f && sceneView.camera.pixelWidth > 0)
                {
                    return sceneView.camera.pixelWidth / ppp;
                }
            }

            return sceneView != null ? sceneView.position.width : 800f;
        }

        private static float GetCanvasHeight(SceneView sceneView)
        {
            if (sceneView != null && sceneView.camera != null)
            {
                var ppp = EditorGUIUtility.pixelsPerPoint;
                if (ppp > 0f && sceneView.camera.pixelHeight > 0)
                {
                    return sceneView.camera.pixelHeight / ppp;
                }
            }

            return sceneView != null ? sceneView.position.height : 600f;
        }

        private static void DrawOutlineRect(Rect rect, Color color, float width = 2f)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, width), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + width, width, rect.height - width * 2f), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - width, rect.y + width, width, rect.height - width * 2f), color);
        }
    }
}

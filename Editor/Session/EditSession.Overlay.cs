using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    /// <summary>
    /// シーンビュー上の操作パネル。
    ///
    /// ここに置くのは<b>編集中に手が止まる操作</b>だけに絞っている。格子数・ミラー・
    /// ボックス操作は変形しながら何度も切り替えるが、補間方式や境界固定のように
    /// 一度決めたら触らない設定は Inspector 側にだけ置く。パネルが大きくなるほど
    /// シーンが隠れ、編集そのものの邪魔になるため。
    /// </summary>
    internal partial class EditSession
    {
        private Rect _overlayRect;
        private Vector2 _overlayMaxPosition;
        private static bool _hasCustomOverlayPosition;
        private static Vector2 _overlayPosition;
        private static bool _overlayDragging;
        private static Vector2 _overlayDragOffset;

        private const float OverlayWidth = 340f;
        private const float OverlayMargin = 10f;
        private const float OverlayBottomPadding = 8f;

        /// <summary>中身を測る前の暫定の高さ。実測値が入るまでの 1 フレームだけ使う。</summary>
        private float _overlayHeight = 240f;

        // Layout イベント時に固定する、レイアウト構成に影響する状態
        private bool _overlayBoxMode;

        /// <summary>
        /// パネルの矩形を決める。
        ///
        /// ハンドル処理より<b>先に</b>呼ぶ必要がある。パネルは最後に（＝ラティスやハンドルの上に）
        /// 描くが、その下のハンドルに操作が抜けないよう、当たり判定は先に確定させておく
        /// （→ <see cref="OnSceneGui"/>、<see cref="DrawOverlay"/>）。
        /// </summary>
        private void UpdateOverlayLayout(SceneView sceneView)
        {
            // Layout と Repaint で GUILayout の構成が変わると
            // 「Getting control N's position in a group with only M controls」で例外になる。
            // Refresh は毎イベント走って警告状態を書き換えるため、レイアウトに影響する状態は
            // Layout イベント時に固定してから両イベントで使い回す。
            if (Event.current.type == EventType.Layout)
            {
                _overlayShowsWarning = _showFallbackWarning;
                _overlayBoxMode = _boxMode;
            }

            var canvasWidth = GetCanvasWidth(sceneView);
            var canvasHeight = GetCanvasHeight(sceneView);

            _overlayMaxPosition = new Vector2(
                Mathf.Max(OverlayMargin, canvasWidth - OverlayWidth - OverlayMargin),
                Mathf.Max(OverlayMargin, canvasHeight - _overlayHeight - OverlayMargin));

            if (!_hasCustomOverlayPosition)
            {
                // デフォルトは右下追従（ウィンドウのリサイズや比率変更にも追従）
                _overlayPosition = _overlayMaxPosition;
            }
            else
            {
                _overlayPosition = Vector2.Min(
                    Vector2.Max(_overlayPosition, new Vector2(OverlayMargin, OverlayMargin)),
                    _overlayMaxPosition);
            }

            _overlayRect = new Rect(_overlayPosition.x, _overlayPosition.y, OverlayWidth, _overlayHeight);
        }

        /// <summary>
        /// パネルを描く。<see cref="OnSceneGui"/> の最後に呼ぶこと。
        /// IMGUI は呼んだ順に描くので、ラティス・ハンドル・矩形選択より後に描かないと
        /// それらがパネルを突き抜けて見える。
        /// </summary>
        /// <param name="blockControl">パネルの下でハンドルを反応させないためのコントロール ID。</param>
        private void DrawOverlay(int blockControl)
        {
            var current = Event.current;

            // HandleUtility.nearestControl は Layout イベントで決まり、距離が同じなら
            // 後から登録した方が勝つ。ここは全ハンドルより後なので、距離 0 で登録すれば
            // パネルの上にカーソルがある間はハンドルが掴まれない
            if (current.type == EventType.Layout && _overlayRect.Contains(current.mousePosition))
            {
                HandleUtility.AddControl(blockControl, 0f);
            }

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
                _overlayPosition = Vector2.Min(
                    Vector2.Max(current.mousePosition - _overlayDragOffset,
                        new Vector2(OverlayMargin, OverlayMargin)),
                    _overlayMaxPosition);
                current.Use();
                GUI.changed = true;
            }
            else if ((current.type == EventType.MouseUp || current.rawType == EventType.MouseUp) && _overlayDragging)
            {
                _overlayDragging = false;
                current.Use();
            }

            Handles.BeginGUI();

            // 背景はほぼ不透明にする。ラティス線やハンドルが透けると文字が読めない
            EditorGUI.DrawRect(_overlayRect, new Color(0.16f, 0.16f, 0.16f, 0.98f));
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

            DrawResolutionSection();
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

            // 高さは中身から測る。行数を数えて定数で持つと、言語やスキンで文字の高さが
            // 変わったときに下端が切れる（BeginArea は範囲外をクリップする）
            if (current.type == EventType.Repaint)
            {
                _overlayHeight = Mathf.Max(60f, GUILayoutUtility.GetLastRect().yMax + OverlayBottomPadding);
            }

            GUILayout.EndArea();
            Handles.EndGUI();

            FlushSettingsUndoGroup();
        }

        /// <summary>
        /// 格子数。<b>ここを変えても形状は変わらない</b>のが本ツールの中核で、
        /// だからこそ編集を中断せずに触れる位置へ置いている。
        /// </summary>
        private void DrawResolutionSection()
        {
            GUILayout.Label(DenLatticeLocalization.Tr("overlay.resolution"), EditorStyles.miniLabel);

            var current = new Vector3Int(_component.ResU, _component.ResV, _component.ResW);
            var next = LatticeResolutionField.Draw(current);

            if (next != current) ChangeResolution(next.x, next.y, next.z);
        }

        private void DrawMirrorSection()
        {
            GUILayout.Label(DenLatticeLocalization.Tr("overlay.mirror"), EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();

            var axes = new[] { LatticeAxis.U, LatticeAxis.V, LatticeAxis.W };
            var names = new[] { "X", "Y", "Z" };
            var previousColor = GUI.backgroundColor;
            var activeColor = new Color(0.35f, 0.95f, 0.45f);

            for (var i = 0; i < axes.Length; i++)
            {
                var isSelected = _component.mirror && _component.mirrorAxis == axes[i];
                GUI.backgroundColor = isSelected ? activeColor : previousColor;

                if (GUILayout.Button(names[i], GUILayout.Height(22)))
                {
                    RecordSettingsChange();
                    if (isSelected)
                    {
                        _component.mirror = false;
                    }
                    else
                    {
                        _component.mirror = true;
                        _component.mirrorAxis = axes[i];
                    }

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

            DenLatticeUndo.Record(_component, "Dennoko Lattice Settings");
        }

        private void FlushSettingsUndoGroup()
        {
            if (_settingsUndoGroup < 0) return;
            if (Event.current == null || Event.current.rawType != EventType.MouseUp) return;

            DenLatticeUndo.Apply(_component);
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

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    internal partial class EditSession
    {
        // ------------------------------------------------------------------
        // 描画
        //
        // 制御点は空間中に浮いているため、DenMeshEditor の頂点ドットと違って
        // 深度バッファによる遮蔽消去は行わない。体の内側にある制御点まで消えてしまうと
        // 掴めなくなるため、常に手前へ描く。

        private const int CircleSegments = 10;
        private static readonly Vector2[] UnitCircle = PrecomputeUnitCircle(CircleSegments);
        private static readonly Vector3[] PointScratch = new Vector3[CircleSegments];

        /// <summary>制御点マーカーの大きさ。ハンドルサイズに対する倍率。</summary>
        private const float ControlDotScale = 0.035f;

        private static readonly Color BoxColor = new Color(0.35f, 0.8f, 1f, 0.9f);
        private static readonly Color CageColor = new Color(0.45f, 0.75f, 0.95f, 0.35f);
        private static readonly Color ControlColor = new Color(0.85f, 0.9f, 0.95f, 0.9f);
        private static readonly Color SelectedColor = new Color(1f, 0.45f, 0.2f, 1f);
        private static readonly Color HoverColor = new Color(1f, 0.85f, 0.25f, 1f);
        private static readonly Color FrozenColor = new Color(0.45f, 0.45f, 0.5f, 0.5f);

        private readonly List<Vector3> _cageBuffer = new List<Vector3>();
        private readonly List<Vector3> _controlBuffer = new List<Vector3>();
        private readonly List<Vector3> _selectedBuffer = new List<Vector3>();
        private readonly List<Vector3> _frozenBuffer = new List<Vector3>();

        private static Vector2[] PrecomputeUnitCircle(int segments)
        {
            var points = new Vector2[segments];
            var step = Mathf.PI * 2f / segments;
            for (var i = 0; i < segments; i++)
            {
                var angle = i * step;
                points[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }

            return points;
        }

        private void DrawGizmos()
        {
            if (Event.current.type != EventType.Repaint) return;

            var camera = Camera.current;
            if (camera == null) return;

            DrawBox();
            DrawCage();
            DrawControlPoints(camera);
            DrawMarquee();
        }

        /// <summary>ラティスボックスの外形。</summary>
        private void DrawBox()
        {
            var previousMatrix = Handles.matrix;
            var previousColor = Handles.color;

            Handles.matrix = BoxToWorld;
            Handles.color = _boxMode ? new Color(1f, 0.75f, 0.3f, 1f) : BoxColor;
            Handles.DrawWireCube(Vector3.zero, _component.boxSize);

            Handles.matrix = previousMatrix;
            Handles.color = previousColor;
        }

        /// <summary>
        /// 格子のワイヤーフレーム。制御点だけでは格子の構造が読み取れないため、
        /// 隣り合う制御点どうしを結んで見せる。
        /// </summary>
        private void DrawCage()
        {
            var resU = _component.ResU;
            var resV = _component.ResV;
            var resW = _component.ResW;

            var lines = _cageBuffer;
            lines.Clear();

            for (var k = 0; k < resW; k++)
            {
                for (var j = 0; j < resV; j++)
                {
                    for (var i = 0; i < resU; i++)
                    {
                        var from = _controlWorld[_component.ControlIndex(i, j, k)];

                        if (i + 1 < resU)
                        {
                            lines.Add(from);
                            lines.Add(_controlWorld[_component.ControlIndex(i + 1, j, k)]);
                        }

                        if (j + 1 < resV)
                        {
                            lines.Add(from);
                            lines.Add(_controlWorld[_component.ControlIndex(i, j + 1, k)]);
                        }

                        if (k + 1 < resW)
                        {
                            lines.Add(from);
                            lines.Add(_controlWorld[_component.ControlIndex(i, j, k + 1)]);
                        }
                    }
                }
            }

            if (lines.Count == 0) return;

            var previousColor = Handles.color;
            Handles.color = CageColor;

            // 点ごとに 1 描画になる DotHandleCap と違い、DrawLines は配列をまとめて描く。
            // 制御点は最大 1000 点＝格子線 2700 本になりうるので、必ずまとめて渡す
            Handles.DrawLines(lines.ToArray());

            Handles.color = previousColor;
        }

        private void DrawControlPoints(Camera camera)
        {
            var cameraTransform = camera.transform;
            var right = cameraTransform.right;
            var up = cameraTransform.up;

            var normal = _controlBuffer;
            var selected = _selectedBuffer;
            var frozen = _frozenBuffer;

            normal.Clear();
            selected.Clear();
            frozen.Clear();

            for (var i = 0; i < _controlWorld.Length; i++)
            {
                var world = _controlWorld[i];
                var size = HandleUtility.GetHandleSize(world) * ControlDotScale;

                if (!IsMovable(i))
                {
                    AddCircle(frozen, world, right, up, size * 0.7f);
                    continue;
                }

                if (_selected.Contains(i))
                {
                    // 選択中は二重円にして、密集していても見分けられるようにする
                    AddCircle(selected, world, right, up, size);
                    AddCircle(selected, world, right, up, size * 1.7f);
                    continue;
                }

                AddCircle(normal, world, right, up, size);
            }

            var previousColor = Handles.color;

            DrawBuffer(frozen, FrozenColor);
            DrawBuffer(normal, ControlColor);
            DrawBuffer(selected, SelectedColor);

            // ホバー中の 1 点だけは実体のあるドットで示す（掴める位置が分かりやすい）
            if (_hoverControl >= 0 && _hoverControl < _controlWorld.Length && !_selected.Contains(_hoverControl))
            {
                var world = _controlWorld[_hoverControl];
                Handles.color = HoverColor;
                Handles.DotHandleCap(0, world, Quaternion.identity,
                    HandleUtility.GetHandleSize(world) * ControlDotScale, EventType.Repaint);
            }

            Handles.color = previousColor;
        }

        private static void DrawBuffer(List<Vector3> buffer, Color color)
        {
            if (buffer.Count == 0) return;

            Handles.color = color;
            Handles.DrawLines(buffer.ToArray());
        }

        private static void AddCircle(List<Vector3> buffer, Vector3 center, Vector3 right, Vector3 up, float radius)
        {
            var rRight = right * radius;
            var rUp = up * radius;
            var count = UnitCircle.Length;

            for (var i = 0; i < count; i++)
            {
                PointScratch[i] = center + rRight * UnitCircle[i].x + rUp * UnitCircle[i].y;
            }

            for (var i = 0; i < count; i++)
            {
                buffer.Add(PointScratch[i]);
                buffer.Add(PointScratch[(i + 1) % count]);
            }
        }

        private void DrawMarquee()
        {
            if (!_marqueeActive || !_marqueeDragging) return;

            var rect = MarqueeRect;

            Handles.BeginGUI();
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.7f, 1f, 0.12f));
            DrawOutlineRect(rect, new Color(0.4f, 0.8f, 1f, 0.9f), 1f);
            Handles.EndGUI();
        }
    }
}

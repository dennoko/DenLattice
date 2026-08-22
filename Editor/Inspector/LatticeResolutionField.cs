using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    /// <summary>
    /// 格子数の入力欄。軸ごとに <c>U [-][3][+]</c> の形で並べる。
    ///
    /// Inspector とシーンビューのオーバーレイの両方で使うため独立させてある。
    /// 数値欄だけだと「どの欄がどの軸か」が分からず、1 ずつ上げ下げする操作も
    /// キーボードに頼ることになるので、軸名と ± ボタンを常に見せる。
    /// </summary>
    internal static class LatticeResolutionField
    {
        /// <summary>1 軸分の幅。ラベル 12 + ボタン 20 + 数値 28 + ボタン 20 + 要素間の余白 6。</summary>
        internal const float AxisWidth = 90f;

        private static GUIStyle _axisLabelStyle;
        private static GUIStyle _fieldStyle;

        /// <summary>
        /// 3 軸をまとめて描く。戻り値が引数と異なれば変更されている。
        /// </summary>
        internal static Vector3Int Draw(Vector3Int current, float axisWidth = AxisWidth)
        {
            EnsureStyles();

            EditorGUILayout.BeginHorizontal();

            var u = DrawAxis("X", current.x, axisWidth);
            var v = DrawAxis("Y", current.y, axisWidth);
            var w = DrawAxis("Z", current.z, axisWidth);

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            return new Vector3Int(u, v, w);
        }

        private static int DrawAxis(string axis, int value, float width)
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Width(width));

            GUILayout.Label(axis, _axisLabelStyle, GUILayout.Width(12));

            bool decrement;
            using (new EditorGUI.DisabledScope(value <= DenLattice.MinResolution))
            {
                decrement = GUILayout.Button("-", EditorStyles.miniButtonLeft,
                    GUILayout.Width(20), GUILayout.Height(18));
            }

            // Delayed 版を使う。素の IntField は 1 文字打つたびに変更を出すので、
            // 「10」と入れようとすると途中の「1」でクランプされて打ち直しになる
            var typed = EditorGUILayout.DelayedIntField(value, _fieldStyle,
                GUILayout.Width(28), GUILayout.Height(18));

            bool increment;
            using (new EditorGUI.DisabledScope(value >= DenLattice.MaxResolution))
            {
                increment = GUILayout.Button("+", EditorStyles.miniButtonRight,
                    GUILayout.Width(20), GUILayout.Height(18));
            }

            EditorGUILayout.EndHorizontal();

            // ± は数値欄を描いた後で効かせる。先に value を書き換えると、
            // 同じフレームの数値欄が古い値を返して打ち消してしまう
            if (decrement) typed--;
            if (increment) typed++;

            return Mathf.Clamp(typed, DenLattice.MinResolution, DenLattice.MaxResolution);
        }

        private static void EnsureStyles()
        {
            // GUIStyle は OnGUI の中でしか作れないため遅延生成する
            if (_axisLabelStyle == null)
            {
                _axisLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                };
            }

            if (_fieldStyle == null)
            {
                _fieldStyle = new GUIStyle(EditorStyles.numberField)
                {
                    alignment = TextAnchor.MiddleCenter,
                };
            }
        }
    }
}

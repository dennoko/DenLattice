using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    /// <summary>
    /// 1 軸ぶんの基底関数の値と、それが掛かる制御点インデックスの範囲。
    ///
    /// <see cref="Start"/> は負や解像度以上になりうる。参照時に
    /// <c>[0, resolution - 1]</c> へクランプすること（＝端の制御点を複製して
    /// 格子を外側へ延長する扱い）。複数の n が同じインデックスへ落ちるのは正しい挙動で、
    /// 重みはそのぶん合算される。
    /// </summary>
    internal struct AxisWeights
    {
        public int Start;
        public int Count;
        public float W0;
        public float W1;
        public float W2;
        public float W3;

        public float this[int n]
        {
            get
            {
                switch (n)
                {
                    case 0: return W0;
                    case 1: return W1;
                    case 2: return W2;
                    default: return W3;
                }
            }
        }
    }

    /// <summary>
    /// ラティスの基底関数。
    ///
    /// <b>変位空間で定式化している</b>ことが要点。<c>deform(v) = v + Σ B_i(v)·d_i</c> の形なので、
    /// 制御点オフセット d がすべてゼロなら基底の性質によらず厳密に恒等変換になる。
    /// 位置空間の FFD（<c>Σ B_i(v)·P_i</c>）だと、B-Spline が制御点を通らないことや
    /// 境界で基底の和が 1 にならないことがそのまま形状のずれとして出てしまう。
    ///
    /// 3 方式はいずれもテンソル積基底で、違いは 1 軸ぶんの重みの作り方だけ。
    /// </summary>
    internal static class LatticeBasis
    {
        /// <summary>
        /// 正規化パラメータ <paramref name="t"/>（0 = 格子の始端, 1 = 終端）に対する
        /// 1 軸ぶんの基底の値を求める。
        /// </summary>
        internal static AxisWeights Compute(float t, int resolution, InterpolationType type)
        {
            if (resolution < 2)
            {
                return new AxisWeights { Start = 0, Count = 1, W0 = 1f };
            }

            var span = resolution - 1;
            var scaled = Mathf.Clamp(t, 0f, 1f) * span;

            // 端（t = 1）でも cell が範囲内に収まるようにする
            var cell = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, span - 1);
            var f = scaled - cell;

            // 制御点が 3 点未満だと三次のステンシルが片側へ寄りすぎるため線形へ縮退させる
            if (type == InterpolationType.Trilinear || resolution < 3)
            {
                return new AxisWeights
                {
                    Start = cell,
                    Count = 2,
                    W0 = 1f - f,
                    W1 = f,
                };
            }

            var f2 = f * f;
            var f3 = f2 * f;

            if (type == InterpolationType.BSpline)
            {
                // 一様三次 B-Spline。制御点を通らないが最も滑らか
                var oneMinus = 1f - f;
                return new AxisWeights
                {
                    Start = cell - 1,
                    Count = 4,
                    W0 = oneMinus * oneMinus * oneMinus / 6f,
                    W1 = (3f * f3 - 6f * f2 + 4f) / 6f,
                    W2 = (-3f * f3 + 3f * f2 + 3f * f + 1f) / 6f,
                    W3 = f3 / 6f,
                };
            }

            // Catmull-Rom（張力 0.5 の Cardinal スプライン）。制御点を通る
            return new AxisWeights
            {
                Start = cell - 1,
                Count = 4,
                W0 = -0.5f * f3 + f2 - 0.5f * f,
                W1 = 1.5f * f3 - 2.5f * f2 + 1f,
                W2 = -1.5f * f3 + 2f * f2 + 0.5f * f,
                W3 = 0.5f * f3 - 0.5f * f2,
            };
        }
    }
}

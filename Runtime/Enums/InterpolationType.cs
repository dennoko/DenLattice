namespace Dennokoworks.DenLattice
{
    /// <summary>
    /// ラティスの補間方式。いずれもテンソル積基底で、基底行列が違うだけ。
    ///
    /// 変位空間で定式化する（<c>deform(v) = v + Σ B_i(v)·d_i</c>）ため、
    /// 制御点オフセットがすべてゼロなら基底によらず厳密に恒等変換になる。
    /// </summary>
    public enum InterpolationType
    {
        /// <summary>三線形。制御点を通り、影響範囲が隣接セルに限られる。</summary>
        Trilinear,

        /// <summary>三次 B-Spline。制御点を通らない（近似）が最も滑らか。</summary>
        BSpline,

        /// <summary>三次 Catmull-Rom。制御点を通る（補間）滑らかな曲線。</summary>
        CatmullRom,
    }
}

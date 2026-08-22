namespace Dennokoworks.DenLattice
{
    /// <summary>
    /// ラティスボックスのローカル軸。ミラー編集の対称軸として使う。
    ///
    /// アバタールートではなくボックス自身の軸を基準にするのは、格子が離散インデックスを
    /// 持つため相手の制御点が自明に決まり、許容誤差というパラメータが不要になるため。
    /// </summary>
    public enum LatticeAxis
    {
        /// <summary>ボックスローカルの X 軸。</summary>
        U,

        /// <summary>ボックスローカルの Y 軸。</summary>
        V,

        /// <summary>ボックスローカルの Z 軸。</summary>
        W,
    }
}

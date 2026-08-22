using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    /// <summary>
    /// シーンビューでのラティス編集セッション。
    ///
    /// 変形の基準となる頂点位置は、NDMF プレビューのプロキシ Renderer から取得する
    /// （<see cref="ProxyRegistry"/>）。プロキシは他ツールのメッシュ変形とボーン操作の両方を
    /// 反映しているため、SkinnedMeshRenderer.BakeMesh でスキニング結果を得ることで
    /// 「実際に見えている形状」を変形できる。
    ///
    /// ドラッグはワールド空間で発生するが、デルタはメッシュローカル空間で保存するため、
    /// スキニング行列の逆行列で変換する。
    ///
    /// 構造は DenMeshEditor の編集セッションと同じで、違いは「ハンドルの変位を
    /// どう頂点へ配分するか」だけ。距離＋減衰カーブの代わりにラティスの基底関数を使う
    /// （→ <see cref="BuildInfluences"/>）。
    /// </summary>
    internal partial class EditSession
    {
        /// <summary>制御点のクリック判定半径（ピクセル）。</summary>
        private const float PickThresholdPixels = 20f;

        private const float RefreshIntervalSeconds = 0.1f;

        /// <summary>クリックと矩形選択を分ける移動量（ピクセル）。</summary>
        private const float MarqueeThresholdPixels = 4f;

        /// <summary>
        /// プロキシ未取得の警告を出すまでの猶予。パイプライン再構築の数フレームで
        /// 警告が明滅しないようにする。
        /// </summary>
        private const double FallbackWarningDelaySeconds = 1.0;

        internal enum BoxTool
        {
            Move,
            Rotate,
            Scale,
        }

        private static EditSession _active;
        private static bool _toolsHiddenBefore;

        /// <summary>
        /// 編集中のコンポーネント。NDMF プレビューフィルタがこの値を監視し、
        /// 編集セッションの開始・終了でプロキシの生成対象を切り替える。
        /// </summary>
        internal static readonly PublishedValue<DenLattice> ActiveComponent =
            new PublishedValue<DenLattice>(null, "DenLattice.ActiveComponent");

        internal static EditSession Active => _active;

        internal DenLattice Component => _component;

        internal static bool IsActive(DenLattice component)
        {
            return _active != null && _active._component == component;
        }

        /// <summary>
        /// エディタのライフサイクルに合わせてセッションを確実に閉じる。
        ///
        /// これが無いと、ドメインリロード時に <see cref="Cleanup"/> が走らず
        /// BakeScratch（HideAndDontSave な Mesh）がエディタ再起動まで残り、
        /// Tools.hidden も戻らない。Enter Play Mode Options でドメインリロードを
        /// 無効にしている環境では static が生き残るため、プレイモード中もセッションが
        /// 動き続けてコンポーネントを書き換えてしまう。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void InstallLifecycleHooks()
        {
            AssemblyReloadEvents.beforeAssemblyReload += End;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            EditorApplication.quitting += End;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            End();
        }

        private static void OnSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            End();
        }

        private static void OnUndoRedoPerformed()
        {
            if (_active == null) return;

            _active._resyncPending = true;
        }

        internal static void Begin(DenLattice component)
        {
            End();
            if (component == null) return;

            _active = new EditSession(component);
            SceneView.duringSceneGui += _active.OnSceneGui;

            // シーンビューの再描画は描画ループの外側から要求する（理由は OnEditorUpdate）
            EditorApplication.update += _active.OnEditorUpdate;

            _toolsHiddenBefore = Tools.hidden;
            Tools.hidden = true;

            // 変形前の形状で描かれる選択アウトラインが変形結果に重なるのを避ける
            SelectionOutline.Suppress();

            // プレビューフィルタへ「編集開始」を伝え、プロキシを生成させる
            ActiveComponent.Value = component;

            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        internal static void End()
        {
            if (_active == null) return;

            SceneView.duringSceneGui -= _active.OnSceneGui;
            EditorApplication.update -= _active.OnEditorUpdate;

            // 後始末は finally に置く。End は beforeAssemblyReload からも呼ばれるため、
            // 上書きインポートの最中など Cleanup が途中で失敗する状況がありうる。
            try
            {
                _active.Cleanup();
            }
            finally
            {
                _active = null;

                // 開始前の状態へ戻す（ユーザーが自分でツールを隠していた場合を潰さない）
                Tools.hidden = _toolsHiddenBefore;
                SelectionOutline.Restore();
                ActiveComponent.Value = null;

                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }

        /// <summary>
        /// Inspector から設定が書き換わったことを伝える。
        /// 補間方式・ミラー・境界固定はいずれも影響範囲の計算に効く。
        /// </summary>
        internal static void NotifySettingsChanged()
        {
            if (_active == null) return;

            // 格子数が Inspector 側で変わっていれば作業配列も作り直す
            if (_active._controlOffsets.Length != _active._component.ControlCount)
            {
                _active.ClearSelection();
                _active.SyncOffsetsFromComponent();
            }

            // 境界固定は「動かせる制御点」の集合を変える。固定された点が選択に残ると
            // 掴めない点が選択されて見えるので、選び直させる
            if (_active._component.freezeBorder) _active.PruneFrozenSelection();

            _active.RecomputeCenter();
            _active.BuildInfluences();

            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------

        private readonly DenLattice _component;
        private double _lastRefresh;

        /// <summary>Undo / Redo を受けて作業状態を作り直す必要があるか。</summary>
        private bool _resyncPending;

        private Vector3 _centerWorld;
        private Vector3 _handlePosition;
        private bool _dragging;

        /// <summary>
        /// 移動ハンドルを掴んでいるか。<see cref="_dragging"/> と違い、掴んだだけで
        /// まだ動かしていない状態も含む。
        /// </summary>
        private bool _handleGrabbed;

        private int _hoverControl = -1;

        // 矩形選択
        private bool _marqueeActive;
        private bool _marqueeDragging;
        private Vector2 _marqueeStart;
        private Vector2 _marqueeCurrent;

        // ボックス編集
        private bool _boxMode;
        private BoxTool _boxTool = BoxTool.Move;
        private bool _boxDragging;
        private Vector3 _pendingBoxPosition;
        private Quaternion _pendingBoxRotation = Quaternion.identity;
        private Vector3 _pendingBoxSize = Vector3.one;

        // シーンビューの定期再描画
        private double _lastRepaint;

        // プロキシ未取得の状態がどれだけ続いているか
        private double _fallbackSince = -1;
        private bool _showFallbackWarning;

        /// <summary>オーバーレイのレイアウトに影響する状態。Layout イベント時に固定する。</summary>
        private bool _overlayShowsWarning;

        /// <summary>オーバーレイのスライダー操作を Undo 1 段にまとめるためのグループ番号。</summary>
        private int _settingsUndoGroup = -1;

        /// <summary>プロキシを取得できていない対象があるか（生の状態）。</summary>
        internal bool AnyFallback { get; private set; }

        /// <summary>
        /// 下流の NDMF フィルタが頂点数を変えているためにプロキシを使えない対象があるか。
        /// この状態では他ツールの影響を反映した変形ができない。
        /// </summary>
        internal bool AnyVertexCountMismatch { get; private set; }

        /// <summary>
        /// ユーザーへ警告を出すべきか。パイプライン再構築の数フレームで明滅しないよう、
        /// フォールバック状態が一定時間続いたときだけ true になる。
        /// </summary>
        internal bool ShowFallbackWarning => _showFallbackWarning;

        internal bool BoxMode
        {
            get => _boxMode;
            set
            {
                if (_boxMode == value) return;

                _boxMode = value;
                if (_boxMode) ClearSelection();
                SceneView.RepaintAll();
            }
        }

        internal BoxTool ActiveBoxTool
        {
            get => _boxTool;
            set => _boxTool = value;
        }

        private EditSession(DenLattice component)
        {
            _component = component;

            Refresh(true);

            // 一度も配置していなければ対象のバウンズへ合わせる。
            // 追加直後からそのまま掴める状態にしておく
            if (!component.boxInitialized)
            {
                AutoFitBox();
            }

            SyncOffsetsFromComponent();
        }

        private void Cleanup()
        {
            // 未確定のドラッグ内容は破棄し、プレビューをコンポーネントの内容へ戻す
            LiveEdits.Clear();

            DisposeTargets();
            _influences.Clear();
            _selected.Clear();

            ProxyRegistry.Prune();
        }

        /// <summary>
        /// Undo / Redo の後に、作業状態をコンポーネントの現在値から作り直す。
        ///
        /// これを行わないと、巻き戻ったコンポーネントに対して古い Working / Snapshot が
        /// 残ったままになり、次のドラッグの <see cref="Commit"/> で「取り消したはずの変形」を
        /// 書き戻してしまう。
        /// </summary>
        private void ResyncFromComponent()
        {
            if (_component == null)
            {
                End();
                return;
            }

            // 巻き戻し後の状態と食い違う未確定データを捨て、プレビューへ更新を促す。
            // パイプラインの再構築を挟まず、生成済みメッシュの頂点だけが書き換わる経路
            ClearLiveEdits();

            _dragging = false;
            _handleGrabbed = false;
            _boxDragging = false;
            _marqueeActive = false;
            _marqueeDragging = false;
            _settingsUndoGroup = -1;

            // MeshEdit のインスタンスが差し替わっていれば、SyncTargetList が
            // ターゲットごと作り直す（このとき選択も解除される）。
            // Undo ではアバターの姿勢や元メッシュは変わらないため、頂点キャッシュを再利用して BakeMesh をスキップする
            Refresh(true, allowCachedVertices: true);

            // インスタンスが維持された場合は作業状態だけを作り直す
            foreach (var target in _targets)
            {
                if (target.Edit != null) target.Edit.CopyTo(target.Working);
                else target.Working.Clear();

                CopyDeltas(target.Working, target.Snapshot);
                target.Touched = false;
            }

            // 格子数が巻き戻ると制御点の対応が変わるので、選択を捨てる
            if (_controlOffsets.Length != _component.ControlCount)
            {
                ClearSelection();
            }

            // ボックスが巻き戻った可能性があるので、ボックス内判定は取り直す
            _paramsValid = false;

            SyncOffsetsFromComponent();
            RecomputeCenter();
            BuildInfluences();

            SceneView.RepaintAll();
        }

        /// <summary>
        /// シーンビューの定期再描画。
        ///
        /// Layout イベントの中で <c>sceneView.Repaint()</c> を呼ぶと
        /// Repaint → OnGUI → Layout → Repaint の無限ループになり、編集中ずっと
        /// 全力で再描画し続けてしまう。描画ループの外側から一定間隔で要求する。
        /// </summary>
        private void OnEditorUpdate()
        {
            // Undo / Redo の後始末。描画ループの外側で、1 フレームにつき 1 回だけ行う
            if (_resyncPending)
            {
                _resyncPending = false;
                ResyncFromComponent();
            }

            // ドラッグ中はハンドル操作自体が再描画を駆動するので不要
            if (_dragging || _boxDragging) return;

            if (EditorApplication.timeSinceStartup - _lastRepaint < RefreshIntervalSeconds) return;
            _lastRepaint = EditorApplication.timeSinceStartup;

            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------
        // シーンビュー

        private void OnSceneGui(SceneView sceneView)
        {
            if (_component == null)
            {
                End();
                return;
            }

            // 別の無関係なオブジェクトを選択したら編集モードを抜ける（ツール状態を残さないため）。
            // コンポーネント自身または対象 Renderer のいずれかが選択されている間は編集を維持する
            var isSelected = Selection.Contains(_component.gameObject);
            if (!isSelected)
            {
                foreach (var edit in _component.edits)
                {
                    if (edit?.target != null && Selection.Contains(edit.target.gameObject))
                    {
                        isSelected = true;
                        break;
                    }
                }
            }

            if (!isSelected)
            {
                End();
                return;
            }

            var current = Event.current;

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                if (_hasSelection)
                {
                    ClearSelection();
                    sceneView.Repaint();
                }
                else
                {
                    End();
                }

                current.Use();
                return;
            }

            if (!_dragging && !_boxDragging) Refresh(false);

            // 格子数がコード外から変わっていた場合に作業配列を追従させる
            if (_controlOffsets.Length != _component.ControlCount)
            {
                ClearSelection();
                SyncOffsetsFromComponent();
            }

            // 何もヒットしなかった場合に拾うためのフォールバックコントロール。
            // MouseDown 時にこれが nearestControl であれば、ハンドル上ではないと判断できる。
            var defaultControl = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(defaultControl);

            // パネルは最後に描く（＝ラティスやハンドルの上に重ねる）が、当たり判定は
            // ハンドル処理より先に必要なので、矩形の計算だけ先に済ませておく
            UpdateOverlayLayout(sceneView);

            HandleBoxTransform(current);
            HandleSelection(current, defaultControl);
            HandleDrag(current);
            DrawGizmos();
            DrawOverlay(defaultControl);

            // Layout イベントで Repaint を呼ぶと無限再描画になるため、ここではホバー追従が
            // 必要なマウス移動時だけにする。定期更新は OnEditorUpdate が担当する。
            if (current.type == EventType.MouseMove || current.type == EventType.MouseDrag)
            {
                sceneView.Repaint();
            }
        }

        // ------------------------------------------------------------------
        // 制御点の選択

        private void HandleSelection(Event current, int defaultControl)
        {
            if (_boxMode) return;
            if (_overlayRect.Contains(current.mousePosition)) return;

            switch (current.type)
            {
                case EventType.MouseMove:
                    if (!_dragging) _hoverControl = PickControl(current.mousePosition);
                    break;

                case EventType.MouseDown:
                    if (current.button != 0 || current.alt) break;

                    // 移動ハンドルを掴もうとしているときは選択処理を行わない
                    if (HandleUtility.nearestControl != defaultControl) break;

                    _marqueeActive = true;
                    _marqueeDragging = false;
                    _marqueeStart = current.mousePosition;
                    _marqueeCurrent = _marqueeStart;

                    // MouseDrag / MouseUp を確実に受け取るために掴んでおく
                    GUIUtility.hotControl = defaultControl;
                    current.Use();
                    break;

                case EventType.MouseDrag:
                    if (!_marqueeActive) break;

                    _marqueeCurrent = current.mousePosition;
                    if ((_marqueeCurrent - _marqueeStart).sqrMagnitude
                        > MarqueeThresholdPixels * MarqueeThresholdPixels)
                    {
                        _marqueeDragging = true;
                    }

                    current.Use();
                    break;

                case EventType.MouseUp:
                    if (!_marqueeActive) break;

                    _marqueeActive = false;

                    // Shift / Ctrl（Mac は Cmd）で追加選択
                    var additive = current.shift || EditorGUI.actionKey;

                    if (_marqueeDragging) SelectInRect(MarqueeRect, additive);
                    else SelectAt(current.mousePosition, additive);

                    _marqueeDragging = false;

                    if (GUIUtility.hotControl == defaultControl) GUIUtility.hotControl = 0;
                    current.Use();
                    break;
            }
        }

        private Rect MarqueeRect
        {
            get
            {
                var min = Vector2.Min(_marqueeStart, _marqueeCurrent);
                var max = Vector2.Max(_marqueeStart, _marqueeCurrent);
                return new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            }
        }

        /// <summary>クリック位置に最も近い制御点。しきい値の外なら -1。</summary>
        private int PickControl(Vector2 mousePosition)
        {
            var best = -1;
            var bestDistance = PickThresholdPixels * PickThresholdPixels;

            for (var i = 0; i < _controlWorld.Length; i++)
            {
                if (!IsMovable(i)) continue;
                if (!TryProject(_controlWorld[i], out var screen)) continue;

                var distance = (screen - mousePosition).sqrMagnitude;
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = i;
            }

            return best;
        }

        /// <summary>
        /// ワールド座標を GUI 座標へ射影する。カメラの後ろにある点は false。
        /// 制御点は多くても 1000 点なので、キャッシュせず素直に射影して構わない。
        /// </summary>
        private static bool TryProject(Vector3 world, out Vector2 screen)
        {
            screen = Vector2.zero;

            var camera = Camera.current;
            if (camera == null) return false;

            if (Vector3.Dot(world - camera.transform.position, camera.transform.forward) <= 0f) return false;

            screen = HandleUtility.WorldToGUIPoint(world);
            return true;
        }

        private void SelectAt(Vector2 mousePosition, bool additive)
        {
            var picked = PickControl(mousePosition);

            if (picked < 0)
            {
                if (!additive) ClearSelection();
                return;
            }

            if (additive)
            {
                if (!_selected.Add(picked)) _selected.Remove(picked);
            }
            else
            {
                _selected.Clear();
                _selected.Add(picked);
            }

            OnSelectionChanged();
        }

        private void SelectInRect(Rect rect, bool additive)
        {
            if (!additive) _selected.Clear();

            for (var i = 0; i < _controlWorld.Length; i++)
            {
                if (!IsMovable(i)) continue;
                if (!TryProject(_controlWorld[i], out var screen)) continue;
                if (!rect.Contains(screen)) continue;

                _selected.Add(i);
            }

            OnSelectionChanged();
        }

        private void OnSelectionChanged()
        {
            if (!_hasSelection)
            {
                ClearSelection();
                return;
            }

            RecomputeCenter();
            BuildInfluences();

            // 次のドラッグの基準を現在の状態に取り直す
            CommitSnapshot();

            SceneView.RepaintAll();
        }

        private void ClearSelection()
        {
            // ドラッグ中に Esc で解除されうる。ここで降ろさないと _dragging が立ちっぱなしになり、
            // 頂点位置の更新（Refresh）が二度と走らなくなる
            _dragging = false;
            _handleGrabbed = false;

            _selected.Clear();
            _influences.Clear();
            _hoverControl = -1;
        }

        // ------------------------------------------------------------------
        // ボックスの移動・回転・スケール

        private void HandleBoxTransform(Event current)
        {
            if (!_boxMode) return;

            var root = SpaceRoot;

            if (!_boxDragging)
            {
                _pendingBoxPosition = _component.boxPosition;
                _pendingBoxRotation = _component.boxRotation;
                _pendingBoxSize = _component.boxSize;
            }

            var worldPosition = root.TransformPoint(_pendingBoxPosition);
            var worldRotation = root.rotation * _pendingBoxRotation;

            EditorGUI.BeginChangeCheck();

            switch (_boxTool)
            {
                case BoxTool.Rotate:
                {
                    var rotated = Handles.RotationHandle(worldRotation, worldPosition);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _pendingBoxRotation = Quaternion.Inverse(root.rotation) * rotated;
                        _boxDragging = true;
                    }

                    break;
                }

                case BoxTool.Scale:
                {
                    var size = Handles.ScaleHandle(_pendingBoxSize, worldPosition, worldRotation,
                        HandleUtility.GetHandleSize(worldPosition));
                    if (EditorGUI.EndChangeCheck())
                    {
                        _pendingBoxSize = size;
                        _boxDragging = true;
                    }

                    break;
                }

                default:
                {
                    var moved = Handles.PositionHandle(worldPosition, worldRotation);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _pendingBoxPosition = root.InverseTransformPoint(moved);
                        _boxDragging = true;
                    }

                    break;
                }
            }

            if (!_boxDragging) return;

            // 未確定のボックスに合わせてケージを組み直す。
            // これが無いとハンドルだけが動いてボックスと制御点が取り残される
            RebuildControlWorld();

            // Handles は hotControl を持った状態で MouseUp を消費するため、
            // Event.type では検出できない（→ HandleDrag のコメント）
            if (GUIUtility.hotControl != 0 && current.rawType != EventType.MouseUp) return;

            _boxDragging = false;

            ApplyBoxTransform(_pendingBoxPosition, _pendingBoxRotation, _pendingBoxSize,
                "Dennoko Lattice Box");
        }

        // ------------------------------------------------------------------
        // 制御点のドラッグ

        private void HandleDrag(Event current)
        {
            if (_boxMode || !_hasSelection) return;

            EditorGUI.BeginChangeCheck();

            // ハンドルが hotControl を取った瞬間＝掴んだ瞬間。PositionHandle の前後で比べる
            // ことで、掴んだのが自分のハンドルかどうかを取り違えずに判定できる
            var hotBefore = GUIUtility.hotControl;

            // Tools.pivotRotation（Global / Local）に追従させる
            var moved = Handles.PositionHandle(_handlePosition, Tools.handleRotation);
            if (EditorGUI.EndChangeCheck())
            {
                _handlePosition = moved;
                _dragging = true;
                ApplyDisplacement();
            }

            if (hotBefore == 0 && GUIUtility.hotControl != 0) _handleGrabbed = true;

            if (!_dragging && !_handleGrabbed) return;

            // Handles.PositionHandle は hotControl を持った状態で MouseUp を受け取ると
            // evt.Use() を呼ぶ。Event.current は同一インスタンスなので、ここへ来た時点で
            // current.type は EventType.Used になっている。
            // つまり type == MouseUp で判定すると確定処理が永久に走らず、
            //   - ドラッグ結果がコンポーネントへ書き込まれない
            //   - _dragging が立ちっぱなしで Refresh も止まる
            //   - 編集終了時に Cleanup の LiveEdits.Clear() で変形が消える
            // という壊れ方をする。Use() の影響を受けない rawType と、
            // hotControl が落ちたことの両方で検出する。
            if (GUIUtility.hotControl != 0 && current.rawType != EventType.MouseUp) return;

            _handleGrabbed = false;
            if (!_dragging) return;

            _dragging = false;
            Commit();

            // プレビューの再構築は非同期なので、プロキシの更新を待たずに
            // 「ハンドルの現在位置」を次の基準にする（タイミングに依存させない）
            CommitSnapshot();
            _centerWorld = _handlePosition;
        }

        /// <summary>
        /// ドラッグ結果をコンポーネントへ書き込んで確定する。
        ///
        /// 「ハンドルを掴んで動かす 1 動作 = Undo 1 段」にするため、書き込みは
        /// マウスを離したときの 1 回だけにし（ドラッグ中は <see cref="LiveEdits"/> 経由）、
        /// さらに Undo グループを明示的に切る。
        ///
        /// 頂点デルタと制御点オフセットは同じ 1 段に入れる。片方だけ巻き戻ると、
        /// 制御点ケージと実際の形状が食い違ってしまう。
        /// </summary>
        private void Commit()
        {
            if (_component == null) return;

            // 書き込むものが無ければ Undo エントリも作らない。
            // 空の段が積まれると、Ctrl+Z を押しても何も起きないように見える。
            //
            // 制御点だけが動いた場合（ボックス内に頂点が無い領域を触った場合）も
            // 確定対象に含める。ここで落とすとケージの位置が保存されず、
            // 次の Refresh でコンポーネントの値へ戻ってしまう
            var hasChanges = ControlOffsetsChanged();
            foreach (var target in _targets)
            {
                if (!target.Touched || ResolveVertexCount(target) <= 0) continue;
                hasChanges = true;
                break;
            }

            if (!hasChanges)
            {
                ClearLiveEdits();
                return;
            }

            BeginUndoGroup("Dennoko Lattice");

            foreach (var target in _targets)
            {
                if (!target.Touched) continue;

                var vertexCount = ResolveVertexCount(target);
                if (vertexCount <= 0) continue;

                target.Edit.SetFrom(target.Working, vertexCount);
            }

            for (var i = 0; i < _controlOffsets.Length; i++)
            {
                _component.SetControlOffset(i, _controlOffsets[i]);
            }

            EndUndoGroup();

            // 確定したので、プレビューはコンポーネントの内容を読むようになる
            ClearLiveEdits();
        }

        /// <summary>
        /// 未確定データを捨て、プレビューへ「読み直せ」と伝える。
        ///
        /// 編集セッション中のコンポーネントは NDMF から監視されていない
        /// （理由は <c>DenLatticePreviewFilter.ObserveEdits</c>）ため、コンポーネントを
        /// 書き換えただけではプレビューが追従しない。更新の合図はこちらから出す。
        /// </summary>
        private static void ClearLiveEdits()
        {
            // Clear() は捨てるものがあったときだけ通知する。両方を無条件に呼ぶと
            // 通知が二重になり、下流上書き構成では間引き待ちが 1 回余分に積まれる
            if (!LiveEdits.Clear()) LiveEdits.Invalidate();
        }
    }
}

using System.Windows.Forms;

namespace rans0m;

/// <summary>
/// Mutable state for a single ransom encounter.
/// </summary>
/// <remarks>
/// This used to live as a dozen public static fields on <c>Global</c>. That made the state
/// impossible to reason about: nothing declared who owned a value or when it changed, and the
/// app had exactly one encounter at a time only by accident. Keeping it in one instance means
/// <see cref="Reset"/> is the single place that returns the app to its ready state.
/// </remarks>
public sealed class RansomState
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _usedCoins = new(StringComparer.Ordinal);

    private bool _canAttack = true;
    private bool _underRansom;
    private bool _crucifixUsed;
    private int _ransomLeft;
    private int _ransomTimeLeft;

    /// <summary>Cash still owed to pay off the current ransom.</summary>
    public int RansomLeft
    {
        get { lock (_gate) { return _ransomLeft; } }
        set { lock (_gate) { _ransomLeft = value; } }
    }

    /// <summary>Seconds left on the ransom countdown.</summary>
    public int RansomTimeLeft
    {
        get { lock (_gate) { return _ransomTimeLeft; } }
        set { lock (_gate) { _ransomTimeLeft = value; } }
    }

    /// <summary>True while the ransom countdown is running and the user still owes money.</summary>
    public bool UnderRansom
    {
        get { lock (_gate) { return _underRansom; } }
        set { lock (_gate) { _underRansom = value; } }
    }

    /// <summary>False once an encounter has started, so a second one cannot begin mid-flight.</summary>
    public bool CanAttack
    {
        get { lock (_gate) { return _canAttack; } }
        set { lock (_gate) { _canAttack = value; } }
    }

    /// <summary>True when the current ransom was cleared by a crucifix rather than by coins.</summary>
    public bool CrucifixUsed
    {
        get { lock (_gate) { return _crucifixUsed; } }
        set { lock (_gate) { _crucifixUsed = value; } }
    }

    /// <summary>
    /// Coin ids already spent in this encounter, tracked so a user cannot copy one coin file
    /// and drag the same id in repeatedly. Not a security boundary, just an anti-cheat nudge.
    /// </summary>
    public IReadOnlyCollection<string> UsedCoins
    {
        get { lock (_gate) { return _usedCoins.ToArray(); } }
    }

    /// <summary>
    /// Records a coin as spent. Returns false if this id was already used.
    /// </summary>
    public bool TrySpendCoin(string coinId)
    {
        lock (_gate)
        {
            return _usedCoins.Add(coinId);
        }
    }

    /// <summary>Returns the app to its ready state, discarding any in-progress ransom.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _canAttack = true;
            _underRansom = false;
            _crucifixUsed = false;
            _ransomLeft = 0;
            _ransomTimeLeft = 0;
            _usedCoins.Clear();
        }

        RansomPayed = null;
        MouseSpy = new MouseSpy();
    }

    /// <summary>
    /// Raised when the ransom is settled, so the encounter can tear down its music and timers.
    /// </summary>
    public Action? RansomPayed { get; set; }

    /// <summary>
    /// Watches the cursor during the warning phase to decide whether the user flinched.
    /// </summary>
    public MouseSpy MouseSpy { get; set; } = new();
}

/// <summary>
/// Tracks cursor position across the warning phase so movement can be detected.
/// </summary>
/// <remarks>
/// Running this from the UI thread is not enough: the keyboard hook fires on the hook thread,
/// and it has to invalidate the recorded position when a key is pressed so that typing also
/// counts as a flinch.
/// </remarks>
public sealed class MouseSpy
{
    private readonly Lock _gate = new();

    private System.Drawing.Point _lastRegisteredPosition;
    private bool _active;

    /// <summary>True while the warning phase is watching the cursor.</summary>
    public bool IsActive
    {
        get { lock (_gate) { return _active; } }
    }

    /// <summary>Begins watching from the cursor's current position.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _lastRegisteredPosition = System.Windows.Forms.Cursor.Position;
            _active = true;
        }
    }

    /// <summary>Stops watching.</summary>
    public void End()
    {
        lock (_gate)
        {
            _active = false;
        }
    }

    /// <summary>
    /// Invalidates the recorded position so <see cref="HasMoved"/> reports true.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _lastRegisteredPosition = new System.Drawing.Point(-1, -1);
        }
    }

    /// <summary>Whether the cursor moved since <see cref="Begin"/> was called.</summary>
    public bool HasMoved()
    {
        lock (_gate)
        {
            return System.Windows.Forms.Cursor.Position != _lastRegisteredPosition;
        }
    }

    /// <summary>Forwards to the active spy, so typing during the warning phase counts as a flinch.</summary>
    public void OnKeyPressed(Keys key) => Invalidate();
}
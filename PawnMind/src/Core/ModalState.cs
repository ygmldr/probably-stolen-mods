namespace PawnMind.Core
{
    /// <summary>
    /// What the modal readback could establish. <see cref="Unknown"/> is a real
    /// answer: it means the read failed or the stack could not be classified, so
    /// callers must refuse rather than assume the screen is clear (R5.1/F4).
    /// </summary>
    public enum ModalState
    {
        Clear = 0,
        Blocked = 1,
        Unknown = 2
    }
}

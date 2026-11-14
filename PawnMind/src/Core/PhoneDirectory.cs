using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class PhoneContact
    {
        public long Number;
        public string Name = string.Empty;
        public string State = string.Empty;
        public bool Shown;
        public int Cooldown;
        public long AmountSpent;
    }

    public static class PhoneDirectory
    {
        /// <summary>Stable error code for a hidden-contact request that is not allowed.</summary>
        public const string HiddenContactsForbidden = "hidden_contacts_forbidden";

        public static List<PhoneContact> Visible(IEnumerable<PhoneContact> contacts, bool includeHidden)
        {
            var result = new List<PhoneContact>();
            if (contacts == null) return result;

            foreach (PhoneContact contact in contacts)
            {
                if (contact == null) continue;
                if (includeHidden || contact.Shown) result.Add(contact);
            }

            return result;
        }

        /// <summary>
        /// Resolves whether the caller may expand contacts the phone book has not
        /// shown yet. Fail-closed: only an explicit debug profile
        /// (allow_test_items=true) outside recording may see them. Production and
        /// every recording session get the visible set, and asking for hidden
        /// contacts there returns <see cref="HiddenContactsForbidden"/> instead of
        /// silently widening the answer.
        /// </summary>
        public static bool TryResolveIncludeHidden(
            bool requested,
            bool allowTestItems,
            bool recordingArmed,
            out bool includeHidden,
            out string error)
        {
            includeHidden = false;
            error = null;

            if (!requested) return true;

            if (recordingArmed)
            {
                error = HiddenContactsForbidden;
                return false;
            }

            if (!allowTestItems)
            {
                error = HiddenContactsForbidden;
                return false;
            }

            includeHidden = true;
            return true;
        }
    }
}

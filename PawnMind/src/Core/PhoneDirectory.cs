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
    }
}

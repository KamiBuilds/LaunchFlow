using System;
namespace NativeUiDriver {
    public static class ClipboardContract {
        // Sequence numbers are diagnostic, not a payload-identity oracle.
        public static bool CanDispatch(string expected, string actual) {
            return !String.IsNullOrEmpty(expected) && String.Equals(expected, actual, StringComparison.Ordinal);
        }
    }
}

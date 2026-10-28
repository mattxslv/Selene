// Compatibility stub for existing Selene\Helper scheduled tasks. Keep shipping this file so an
// update replaces the old elevated command interpreter instead of leaving it on disk.
// No request files, task installation, process control or native APIs are supported.
namespace Selene
{
    public static class SeleneHelper
    {
        public static int Main(string[] args)
        {
            return 2; // retired: fail closed for every legacy command, including no arguments
        }
    }
}

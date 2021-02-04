namespace CyberCAT.Tests
{
    class Utils
    {
        // Taken from https://sau001.wordpress.com/2019/02/24/net-core-unit-tests-how-to-deploy-files-without-using-deploymentitem/
        internal static string GetFullPathToFile(string pathRelativeUnitTestingFile)
        {
            string folderProjectLevel = GetPathToCurrentUnitTestProject();
            string final = System.IO.Path.Combine(folderProjectLevel, pathRelativeUnitTestingFile);
            return final;
        }
        /// <summary>
        /// Get the path to the current unit testing project.
        /// </summary>
        /// <returns></returns>
        private static string GetPathToCurrentUnitTestProject()
        {
            var pathSeparator = System.IO.Path.PathSeparator;
            string pathAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string folderAssembly = System.IO.Path.GetDirectoryName(pathAssembly);
            if (folderAssembly.EndsWith(pathSeparator) == false) folderAssembly = folderAssembly + pathSeparator;
            string folderProjectLevel = System.IO.Path.GetFullPath(System.IO.Path.Join(folderAssembly, "..", "..", ".."));
            return folderProjectLevel;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Chorizite.OpenGLSDLBackend.Lib {

    internal static class EmbeddedResourceReader {
        internal static string GetEmbeddedResource(string filename) {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Chorizite.OpenGLSDLBackend." + filename;

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found in {assembly.GetName().Name}");
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}

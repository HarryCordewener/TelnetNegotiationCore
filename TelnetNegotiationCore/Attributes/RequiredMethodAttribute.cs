using System;

namespace TelnetNegotiationCore.Attributes
{
    /// <summary>
    /// Attribute to mark a plugin class as requiring specific method calls before initialization.
    /// This serves as executable documentation for plugin consumers, indicating which methods
    /// must be called to properly configure the plugin before InitializeAsync is invoked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This attribute works in conjunction with the TNCP006 analyzer to provide compile-time
    /// documentation of required plugin setup methods. The analyzer will generate informational
    /// messages for any plugin class decorated with this attribute.
    /// </para>
    /// <para>
    /// Example usage:
    /// <code>
    /// [RequiredMethod("SetConfiguration")]
    /// [RequiredMethod("EnableFeature")]
    /// public class MyProtocol : TelnetProtocolPluginBase
    /// {
    ///     public void SetConfiguration(MyConfig config) { /* ... */ }
    ///     public void EnableFeature() { /* ... */ }
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// When the above plugin is compiled, the TNCP006 analyzer will generate:
    /// "Info TNCP006: Plugin 'MyProtocol' requires calls to the following methods before InitializeAsync: SetConfiguration, EnableFeature"
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class RequiredMethodAttribute : Attribute
    {
        /// <summary>
        /// Gets the name of the required method that must be called.
        /// </summary>
        public string MethodName { get; }

        /// <summary>
        /// Gets an optional description of why this method must be called.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RequiredMethodAttribute"/> class.
        /// </summary>
        /// <param name="methodName">
        /// The name of the method that must be called before the plugin is initialized.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="methodName"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="methodName"/> is empty or whitespace.
        /// </exception>
        public RequiredMethodAttribute(string methodName)
        {
            if (methodName == null)
                throw new ArgumentNullException(nameof(methodName));
            
            if (string.IsNullOrWhiteSpace(methodName))
                throw new ArgumentException("Method name cannot be empty or whitespace.", nameof(methodName));

            MethodName = methodName;
        }
    }
}

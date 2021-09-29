using NUnit.Framework;
using TelnetNegotiationCore;

namespace TelnetNegotiationCore.UnitTests
{
	[TestFixture]
	public class Tests
	{
		[SetUp]
		public void Setup()
		{
		}

		[Test]
		public void Test1()
		{
			var ti = new TelnetInterpretor();
			var a = new MSSPConfig() 
			{
				Name = () => "John",
				Ansi = () => true,
				Areas = () => 5
			};
			
			ti.MSSPReadConfig(a);

			Assert.Pass();
		}
	}
}
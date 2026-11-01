using HotelKiosk.Application.Services;
using Xunit;

namespace HotelKiosk.Tests
{
    public class CaptureSourceTests
    {
        [Fact]
        public void Empty_SelectsCamera_WhenNoAgentConfigured()
        {
            // Поставка едет с камерой: AgentScanPath пуст, CaptureSource=camera
            var source = CaptureSource.Resolve("", agentScanPathConfigured: false);

            Assert.True(source.IsCamera);
            Assert.True(source.IsConfigured);
            Assert.Equal("camera", source.Kind);
        }

        [Fact]
        public void Empty_SelectsReader_WhenAgentConfigured()
        {
            var source = CaptureSource.Resolve(null, agentScanPathConfigured: true);

            Assert.False(source.IsCamera);
            Assert.True(source.IsConfigured);
            Assert.Equal("reader", source.Kind);
        }

        [Theory]
        [InlineData("camera")]
        [InlineData("CAMERA")]
        [InlineData(" Camera ")]
        public void ExplicitCamera_Wins(string value)
        {
            // Явное значение важнее наличия AgentScanPath: оператор решил
            Assert.True(CaptureSource.Resolve(value, agentScanPathConfigured: true).IsCamera);
        }

        [Theory]
        [InlineData("reader")]
        [InlineData("READER")]
        public void ExplicitReader_Wins(string value)
        {
            Assert.False(CaptureSource.Resolve(value, agentScanPathConfigured: false).IsCamera);
        }

        [Fact]
        public void ReaderWithoutAgent_IsNotConfigured()
        {
            // Выбрали считыватель, но агент не настроен — шаг скана не пройдёт.
            // Именно эту комбинацию показывает валидатор конфигурации как блокер.
            var source = CaptureSource.Resolve("reader", agentScanPathConfigured: false);

            Assert.False(source.IsConfigured);
        }

        [Fact]
        public void UnknownValue_FallsBackToAuto()
        {
            var source = CaptureSource.Resolve("webcam", agentScanPathConfigured: false);

            Assert.True(source.IsCamera);
        }

        [Fact]
        public void Default_IsConfiguredCamera()
        {
            Assert.True(CaptureSource.Default.IsConfigured);
            Assert.True(CaptureSource.Default.IsCamera);
        }
    }
}

using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Messaging.Events
{
    [TestFixture]
    public class EventAggregatorFixture : TestBase<EventAggregator>
    {
        private Mock<IHandle<EventA>> _handlerA1;
        private Mock<IHandle<EventA>> _handlerA2;

        private Mock<IHandle<EventB>> _handlerB1;
        private Mock<IHandle<EventB>> _handlerB2;
        private Mock<IHandleAsync<EventA>> _asyncHandler;
        private Mock<IHandleAsync<IEvent>> _globalHandler;

        [SetUp]
        public void Setup()
        {
            _handlerA1 = new Mock<IHandle<EventA>>();
            _handlerA2 = new Mock<IHandle<EventA>>();
            _handlerB1 = new Mock<IHandle<EventB>>();
            _handlerB2 = new Mock<IHandle<EventB>>();
            _asyncHandler = new Mock<IHandleAsync<EventA>>();
            _globalHandler = new Mock<IHandleAsync<IEvent>>();

            Mocker.GetMock<IServiceFactory>()
                  .Setup(c => c.BuildAll<IHandle<EventA>>())
                  .Returns(new List<IHandle<EventA>> { _handlerA1.Object, _handlerA2.Object });

            Mocker.GetMock<IServiceFactory>()
                  .Setup(c => c.BuildAll<IHandle<EventB>>())
                  .Returns(new List<IHandle<EventB>> { _handlerB1.Object, _handlerB2.Object });


            Mocker.GetMock<IServiceFactory>()
                  .Setup(c => c.BuildAll<IHandleAsync<EventA>>())
                  .Returns(new List<IHandleAsync<EventA>> { _asyncHandler.Object });

            Mocker.GetMock<IServiceFactory>()
                  .Setup(c => c.BuildAll<IHandleAsync<IEvent>>())
                  .Returns(new List<IHandleAsync<IEvent>> { _globalHandler.Object });
        }

        [Test]
        public void should_publish_event_to_handlers()
        {
            var eventA = new EventA();

            Subject.PublishEvent(eventA);

            _handlerA1.Verify(c => c.Handle(eventA), Times.Once());
            _handlerA2.Verify(c => c.Handle(eventA), Times.Once());
        }

        [Test]
        public void should_not_publish_to_incompatible_handlers()
        {
            var eventA = new EventA();

            Subject.PublishEvent(eventA);

            _handlerA1.Verify(c => c.Handle(eventA), Times.Once());
            _handlerA2.Verify(c => c.Handle(eventA), Times.Once());

            _handlerB1.Verify(c => c.Handle(It.IsAny<EventB>()), Times.Never());
            _handlerB2.Verify(c => c.Handle(It.IsAny<EventB>()), Times.Never());
        }

        [Test]
        public void strict_publish_should_run_sync_async_and_global_handlers_inline()
        {
            var eventA = new EventA();

            Subject.PublishEventStrict(eventA);

            _handlerA1.Verify(c => c.Handle(eventA), Times.Once());
            _handlerA2.Verify(c => c.Handle(eventA), Times.Once());
            _asyncHandler.Verify(c => c.HandleAsync(eventA), Times.Once());
            _globalHandler.Verify(c => c.HandleAsync(eventA), Times.Once());
        }

        [Test]
        public void strict_publish_should_propagate_handler_failure_and_stop()
        {
            var eventA = new EventA();
            _handlerA1.Setup(c => c.Handle(eventA)).Throws(new InvalidOperationException("strict failure"));

            Assert.Throws<InvalidOperationException>(() => Subject.PublishEventStrict(eventA));

            _handlerA2.Verify(c => c.Handle(eventA), Times.Never());
            _asyncHandler.Verify(c => c.HandleAsync(eventA), Times.Never());
            _globalHandler.Verify(c => c.HandleAsync(eventA), Times.Never());
        }

        [Test]
        public void broken_handler_should_not_effect_others_handler()
        {
            var eventA = new EventA();

            _handlerA1.Setup(c => c.Handle(It.IsAny<EventA>()))
                       .Throws(new NotImplementedException());

            Subject.PublishEvent(eventA);

            _handlerA1.Verify(c => c.Handle(eventA), Times.Once());
            _handlerA2.Verify(c => c.Handle(eventA), Times.Once());

            ExceptionVerification.ExpectedErrors(1);
        }
    }

    public class EventA : IEvent
    {
    }

    public class EventB : IEvent
    {
    }
}

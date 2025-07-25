namespace Namotion.Interceptor.GraphQL
{
    public class Subscription<TSubject>
    {
        [Subscribe]
        public TSubject Root([EventMessage] TSubject subject) => subject;
    }
}
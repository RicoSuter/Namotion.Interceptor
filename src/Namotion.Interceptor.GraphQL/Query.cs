namespace Namotion.Interceptor.GraphQL
{
    public class Query<TSubject>
    {
        private readonly TSubject _subject;

        public Query(TSubject subject)
        {
            _subject = subject;
        }

        public TSubject GetRoot() => _subject;
    }
}
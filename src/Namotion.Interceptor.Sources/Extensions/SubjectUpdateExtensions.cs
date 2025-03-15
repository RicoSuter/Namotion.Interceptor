namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdateExtensions
{
    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource? source)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Item is not null)
            {
                if (subject.Properties[propertyName].GetValue?.Invoke(subject) is IInterceptorSubject childSubject)
                {
                    ApplySubjectUpdate(childSubject, propertyUpdate.Item, source);
                }
                // TODO: Implement
            }
            else if (propertyUpdate.Items is not null)
            {
                var childSubjects = subject.Properties[propertyName].GetValue?.Invoke(subject) as IEnumerable<IInterceptorSubject>;
                var i = 0;
                foreach (var item in childSubjects ?? [])
                {
                    ApplySubjectUpdate(item, propertyUpdate.Items[i].Item!, source);
                    i++;
                }
                // TODO: Implement dictionary
            }
            else
            {
                var propertyReference = new PropertyReference(subject, propertyName);
                if (source is not null)
                {
                    propertyReference.SetValueFromSource(source, propertyUpdate.Value);
                }
                else
                {
                    propertyReference.Metadata.SetValue?.Invoke(propertyReference.Subject, propertyUpdate.Value);
                }
            }
        }
    }
}
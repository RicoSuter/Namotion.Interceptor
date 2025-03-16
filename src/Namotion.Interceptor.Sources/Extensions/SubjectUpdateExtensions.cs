using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdateExtensions
{
    public static void VisitSubjectValueUpdates(this IInterceptorSubject subject, SubjectUpdate update, 
        Action<PropertyReference, SubjectPropertyUpdate> applySubjectUpdate)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Item is not null)
            {
                if (subject.GetRegisteredSubjectPropertyValue(propertyName) is IInterceptorSubject childSubject)
                {
                    ApplySubjectUpdate(childSubject, propertyUpdate.Item, applySubjectUpdate);
                }
                // TODO: Implement
            }
            else if (propertyUpdate.Items is not null)
            {
                var childSubjects = subject.Properties[propertyName].GetValue?.Invoke(subject) as IEnumerable<IInterceptorSubject>;
                var i = 0;
                foreach (var item in childSubjects ?? [])
                {
                    ApplySubjectUpdate(item, propertyUpdate.Items[i].Item!, applySubjectUpdate);
                    i++;
                }
                // TODO: Implement dictionary
            }
            else
            {
                var propertyReference = new PropertyReference(subject, propertyName);
                applySubjectUpdate.Invoke(propertyReference, propertyUpdate);
            }
        }
    }


    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update, 
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectValueUpdates(update, (propertyReference, propertyUpdate) =>
        {
            transform?.Invoke(propertyReference, propertyUpdate);
            propertyReference.Metadata.SetValue?.Invoke(propertyReference.Subject, propertyUpdate.Value);
        });
    }

    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource source, 
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectValueUpdates(update, (propertyReference, propertyUpdate) =>
        {
            transform?.Invoke(propertyReference, propertyUpdate);
            propertyReference.SetValueFromSource(source, propertyUpdate.Value);
        });
    }
}
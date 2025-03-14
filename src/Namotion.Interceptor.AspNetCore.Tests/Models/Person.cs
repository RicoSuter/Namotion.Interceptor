﻿using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.AspNetCore.Tests.Models
{
    [InterceptorSubject]
    public partial class Person
    {
        public Person()
        {
            Children = [];
        }

        [MaxLength(4)]
        public partial string? FirstName { get; set; }

        public partial string? LastName { get; set; }

        public partial Person? Father { get; set; }

        public partial Person? Mother { get; set; }

        public partial IReadOnlyCollection<Person> Children { get; set; }
    }
}
using System;
using System.Collections.Generic;

namespace EcfDgii.Client.Shared.Common
{
    public class Result
    {
        public bool IsSuccess { get; }
        public bool IsFailure => !IsSuccess;
        public string? Error { get; }
        public List<string> Errors { get; } = new List<string>();

        protected Result(bool isSuccess, string? error, IEnumerable<string>? errors = null)
        {
            IsSuccess = isSuccess;
            Error = error;
            if (errors != null)
            {
                Errors.AddRange(errors);
            }
            else if (!string.IsNullOrEmpty(error))
            {
                Errors.Add(error);
            }
        }

        public static Result Success() => new Result(true, null);
        public static Result Failure(string error) => new Result(false, error);
        public static Result Failure(IEnumerable<string> errors) => new Result(false, null, errors);
    }

    public class Result<T> : Result
    {
        public T? Value { get; }

        protected Result(bool isSuccess, T? value, string? error, IEnumerable<string>? errors = null)
            : base(isSuccess, error, errors)
        {
            Value = value;
        }

        public static Result<T> Success(T value) => new Result<T>(true, value, null);
        public static new Result<T> Failure(string error) => new Result<T>(false, default, error);
        public static new Result<T> Failure(IEnumerable<string> errors) => new Result<T>(false, default, null, errors);
    }
}

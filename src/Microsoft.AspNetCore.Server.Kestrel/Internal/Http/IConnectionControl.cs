// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Http
{
    public interface IConnectionControl
    {
        void Pause();
        void Resume();
        void End(ProduceEndType endType);
        void SetTimeout(long milliseconds);
        void ResetTimeout(long milliseconds);
        void CancelTimeout();
    }
}
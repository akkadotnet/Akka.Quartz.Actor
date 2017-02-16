using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Akka.Quartz.Actor.Exceptions
{
    public class JobNotFoundException: Exception
    {
        public JobNotFoundException() : base("job not found")
        {            
        }
    }
}

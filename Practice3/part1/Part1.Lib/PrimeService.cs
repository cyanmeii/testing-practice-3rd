namespace async_tests;


public abstract class PrimeService{

    public abstract bool CheckIsPrime(int p); 

    public abstract void ClearCache();
}


public class ConcretePrimeService: PrimeService{
  
  public override bool CheckIsPrime(int p)
  {
    if(p<=0) throw new ArgumentException("Parameter should be positive number");
    if( p>1 && p%2!=0 )
    {
      int bound= (int) Math.Sqrt(p);
      for(int i = 3; i <= bound; i+=2)
      {
        if(p%i==0) 
          return false;

      }
      return true;
    }

    return false;
    
  }

  public override void ClearCache(){}

}
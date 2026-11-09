(module
  ;; 加法函数
  (func $add (param $a i32) (param $b i32) (result i32)
    local.get $a
    local.get $b
    i32.add
  )
  ;; 乘法计算微函数
  (func $calc (param $n i32) (result i32)
    local.get $n
    i32.const 42
    i32.mul
  )
  ;; 默认快速运行函数，返回状态码 100
  (func $run (result i32)
    i32.const 100
  )
  (export "add" (func $add))
  (export "calc" (func $calc))
  (export "run" (func $run))
  (export "main" (func $run))
)
